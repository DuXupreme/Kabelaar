"""UI-opbouw: linkerpanelen, werkbalk, thema/UI-schaal en menubar.

UIBuilderMixin bevat de constructie van de interface en de paneel-/thema-logica.
De hoofdklasse erft ervan; verwijzingen naar self lossen op tegen die instantie.
"""

from __future__ import annotations

import tkinter as tk
from tkinter import font as tkfont, ttk
from typing import List

import theme as ui_theme
import warm_theme
from ui_tooltip import attach as attach_tooltip
from geometry import clamp
from ui_scaling import UI_SCALE_LABELS, normalize_ui_scale_percent
from model import (
    DEFAULT_DIMENSION_OFFSET_MM,
    DEFAULT_LEADER_TEXT_SIZE_PT,
    DIMENSION_ORIENTATION_OPTIONS,
    PAPER_PRESET_OPTIONS,
    WIRE_ENDPOINT_DRAG_SCOPE_OPTIONS,
    WIRE_MOVE_SCOPE_OPTIONS,
    WIRE_STYLE_OPTIONS,
    dimension_orientation_label,
)

try:
    import sv_ttk
except Exception:
    sv_ttk = None


LEFT_PANEL_DEFINITIONS = (
    ("tools", "Start"),
    ("properties", "Eigenschappen"),
    ("symbols", "Bibliotheek"),
    ("project", "Tekening"),
    ("files", "Bestand en export"),
)
UI_NAMED_FONTS = (
    "TkDefaultFont",
    "TkTextFont",
    "TkMenuFont",
    "TkHeadingFont",
    "TkCaptionFont",
    "TkSmallCaptionFont",
    "TkIconFont",
    "TkTooltipFont",
    "TkFixedFont",
    # De eigen named fonts van het warme thema meeschalen, zodat de
    # UI-schaal-functie ook de ttk-widgets en menu's/listboxes raakt.
    *warm_theme.SCALED_FONT_NAMES,
)


class UIBuilderMixin:
    # ---------------- UI ----------------
    def _settings_bool(self, key: str, fallback: bool) -> bool:
        value = self.settings.get(key, fallback)
        return value if isinstance(value, bool) else fallback

    def _settings_bool_from(self, mapping: dict, key: str, fallback: bool) -> bool:
        value = mapping.get(key, fallback)
        return value if isinstance(value, bool) else fallback

    def _default_side_panel_width(self) -> int:
        return self._scaled_ui_int(310, minimum=180)

    def _side_panel_min_width(self) -> int:
        return max(110, self._scaled_ui_int(120, minimum=110))

    def _side_panel_max_width(self) -> int:
        return max(360, self._scaled_ui_int(760, minimum=360))

    def _normalize_side_panel_width(self, value) -> int:
        try:
            width = int(float(value))
        except (TypeError, ValueError):
            width = self._default_side_panel_width()
        return int(clamp(width, self._side_panel_min_width(), self._side_panel_max_width()))

    def _init_control_ui_scaling(self):
        for font_name in UI_NAMED_FONTS:
            try:
                font_obj = tkfont.nametofont(font_name)
                self._base_named_font_sizes[font_name] = int(font_obj.cget("size"))
            except (tk.TclError, ValueError):
                continue

        style = ttk.Style(self)
        for style_name in ("TButton", "TCheckbutton", "TCombobox", "TEntry", "TLabel", "TMenubutton"):
            try:
                self._style_base_padding[style_name] = style.lookup(style_name, "padding")
            except tk.TclError:
                continue

    def _apply_app_theme(self):
        """Pas het warme 'Klei & inkt'-thema toe (vervangt de Fluent-basis)."""
        fonts = warm_theme.apply(self, getattr(self, "_app_theme", "light"))
        self._font_family_ui = fonts["ui"]
        self._font_family_heading = fonts["heading"]
        self._font_family_mono = fonts["mono"]

    # Behoud de oude naam als alias zodat bestaande aanroepen blijven werken.
    _apply_sv_theme = _apply_app_theme

    def _configure_app_style(self):
        t = ui_theme.tokens(getattr(self, "_app_theme", "light"))
        fonts = warm_theme.resolve_fonts(self)
        try:
            warm_theme.configure_ttk(ttk.Style(self), t, fonts)
        except tk.TclError:
            pass

    def _apply_theme_colors(self):
        """Werk de niet-ttk chrome (venster, canvas, grip, sidebar) bij aan het thema."""
        t = ui_theme.tokens(getattr(self, "_app_theme", "light"))
        try:
            self.configure(background=t["app_bg"])
        except tk.TclError:
            pass
        if hasattr(self, "canvas"):
            try:
                self.canvas.configure(background=t["canvas_bg"])
            except tk.TclError:
                pass
        if hasattr(self, "side_panel_resize_grip"):
            try:
                self.side_panel_resize_grip.configure(background=t["grip"])
            except tk.TclError:
                pass
        if hasattr(self, "left_panel_canvas"):
            try:
                self.left_panel_canvas.configure(background=t["app_bg"])
            except tk.TclError:
                pass
        if hasattr(self, "symbol_list"):
            try:
                self.symbol_list.configure(
                    background=t["field_bg"],
                    foreground=t["text"],
                    selectbackground=t["selection_bg"],
                    selectforeground=t["selection_fg"],
                    highlightthickness=0,
                    borderwidth=0,
                )
            except tk.TclError:
                pass

    def set_app_theme(self, name: str):
        name = ui_theme.normalize_theme(name)
        self._app_theme = name
        self.app_theme_var.set(name)
        self._apply_sv_theme()
        self._configure_app_style()
        self._apply_control_ui_scale()
        self._apply_theme_colors()
        self._update_mode_buttons()
        self._save_settings(theme=name)
        self.request_redraw()

    def _ui_scale_factor(self) -> float:
        return normalize_ui_scale_percent(getattr(self, "_ui_scale_percent", 100)) / 100.0

    def _scaled_ui_int(self, value: int, minimum: int = 1) -> int:
        return max(minimum, int(round(value * self._ui_scale_factor())))

    def _scale_padding_value(self, value):
        if isinstance(value, (tuple, list)):
            return tuple(self._scale_padding_value(item) for item in value)
        parts = str(value).split()
        if len(parts) > 1:
            scaled = [str(self._scaled_ui_int(int(float(part)))) if self._looks_numeric(part) else part for part in parts]
            return " ".join(scaled)
        if self._looks_numeric(value):
            return self._scaled_ui_int(int(float(value)))
        return value

    def _looks_numeric(self, value) -> bool:
        try:
            float(str(value))
            return True
        except (TypeError, ValueError):
            return False

    def _apply_control_ui_scale(self):
        factor = self._ui_scale_factor()
        for font_name, base_size in self._base_named_font_sizes.items():
            try:
                sign = -1 if base_size < 0 else 1
                scaled_size = max(6, int(round(abs(base_size) * factor))) * sign
                tkfont.nametofont(font_name).configure(size=scaled_size)
            except tk.TclError:
                continue

        style = ttk.Style(self)
        for style_name, base_padding in self._style_base_padding.items():
            if base_padding not in ("", None):
                try:
                    style.configure(style_name, padding=self._scale_padding_value(base_padding))
                except tk.TclError:
                    pass

        if hasattr(self, "symbol_list"):
            try:
                self.symbol_list.configure(font=tkfont.nametofont("TkTextFont"))
            except tk.TclError:
                pass
        for attr in ("prop_color_preview", "prop_color_b_preview"):
            if hasattr(self, attr):
                getattr(self, attr).configure(width=max(2, self._scaled_ui_int(2, minimum=2)))
        if not getattr(self, "_side_panel_width_user_set", False):
            self.side_panel_width_px = self._normalize_side_panel_width(self._default_side_panel_width())
        if hasattr(self, "left_panel_canvas"):
            self._set_side_panel_width(self.side_panel_width_px, persist=False)
            self.after_idle(self._update_left_panel_scrollregion)

    def _side_panel_width(self) -> int:
        return self._normalize_side_panel_width(getattr(self, "side_panel_width_px", self._default_side_panel_width()))

    def _set_side_panel_width(self, width: int, *, persist: bool = True, user_set: bool = False):
        width = self._normalize_side_panel_width(width)
        self.side_panel_width_px = width
        if user_set:
            self._side_panel_width_user_set = True

        visible = self.side_panel_visible_var.get()
        self.columnconfigure(0, minsize=width if visible else 0)
        self.columnconfigure(1, minsize=6 if visible else 0)
        if hasattr(self, "left_sidebar"):
            self.left_sidebar.configure(width=width)
            if visible:
                self.left_sidebar.grid()
            else:
                self.left_sidebar.grid_remove()
        if hasattr(self, "side_panel_resize_grip"):
            if visible:
                self.side_panel_resize_grip.grid()
            else:
                self.side_panel_resize_grip.grid_remove()
        if hasattr(self, "left_panel_canvas"):
            self.after_idle(self._update_left_panel_scrollregion)
        self._update_wrap_labels(width)
        if persist:
            self._save_settings(side_panel_width_px=width)

    def _on_side_panel_resize_down(self, event):
        self._side_panel_drag_start_x = event.x_root
        self._side_panel_drag_start_width = self._side_panel_width()
        if hasattr(self, "side_panel_resize_grip"):
            self.side_panel_resize_grip.configure(bg=ui_theme.color(self._app_theme, "grip_active"))
        return "break"

    def _on_side_panel_resize_drag(self, event):
        if self._side_panel_drag_start_x is None or self._side_panel_drag_start_width is None:
            return "break"
        delta = event.x_root - self._side_panel_drag_start_x
        self._set_side_panel_width(self._side_panel_drag_start_width + delta, persist=False, user_set=True)
        return "break"

    def _on_side_panel_resize_up(self, _event):
        if self._side_panel_drag_start_width is not None:
            self._set_side_panel_width(self._side_panel_width(), persist=True, user_set=True)
        self._side_panel_drag_start_x = None
        self._side_panel_drag_start_width = None
        if hasattr(self, "side_panel_resize_grip"):
            self.side_panel_resize_grip.configure(bg=ui_theme.color(self._app_theme, "grip"))
        return "break"

    def _create_left_panel_section(self, key: str, parent, row: int):
        label = dict(LEFT_PANEL_DEFINITIONS).get(key, key)
        wrapper = ttk.Frame(parent)
        wrapper.grid(row=row, column=0, sticky="ew", pady=(0, 8))
        wrapper.columnconfigure(0, weight=1)

        # De kop is één gevulde band ("eilandje"): kop-knop + verbergknop delen
        # dezelfde band-achtergrond en sluiten naadloos op elkaar aan.
        header = ttk.Frame(wrapper, style="PanelBand.TFrame")
        header.grid(row=0, column=0, sticky="ew")
        header.columnconfigure(0, weight=1)
        button = ttk.Button(header, style="PanelHeader.TButton", command=lambda k=key: self.toggle_panel_collapsed(k))
        button.grid(row=0, column=0, sticky="ew")
        hide_button = ttk.Button(header, text="x", width=3, style="PanelClose.TButton", command=lambda k=key: self.set_panel_visible(k, False))
        hide_button.grid(row=0, column=1, sticky="nsew")
        attach_tooltip(hide_button, "Dit paneel verbergen (terughalen via Beeld ▸ Panelen)")

        body = ttk.Frame(wrapper)
        body.grid(row=1, column=0, sticky="ew", pady=(6, 0))
        body.columnconfigure(0, weight=1)

        self._panel_sections[key] = {
            "label": label,
            "wrapper": wrapper,
            "body": body,
            "button": button,
            "grid": {"row": row, "column": 0, "sticky": "ew", "pady": (0, 8)},
        }
        return body

    def _apply_panel_layout(self, persist: bool = True):
        visible = self.side_panel_visible_var.get()
        self._set_side_panel_width(self._side_panel_width(), persist=False)

        if hasattr(self, "side_panel_toggle_text_var"):
            self.side_panel_toggle_text_var.set("Panelen <<" if visible else "Panelen >>")

        for key, section in self._panel_sections.items():
            is_visible = self.panel_visible_vars[key].get()
            is_collapsed = self.panel_collapsed_vars[key].get()
            if is_visible:
                section["wrapper"].grid(**section["grid"])
            else:
                section["wrapper"].grid_remove()
            section["body"].grid_remove() if is_collapsed else section["body"].grid()
            state = "[+]" if is_collapsed else "[-]"
            section["button"].configure(text=f"{state} {section['label']}")

        if hasattr(self, "left_panel_canvas"):
            self.after_idle(self._update_left_panel_scrollregion)
        if persist:
            self._save_panel_layout_settings()

    def _save_panel_layout_settings(self):
        self._save_settings(
            side_panel_visible=bool(self.side_panel_visible_var.get()),
            side_panel_width_px=int(self._side_panel_width()),
            visible_panels={key: bool(var.get()) for key, var in self.panel_visible_vars.items()},
            collapsed_panels={key: bool(var.get()) for key, var in self.panel_collapsed_vars.items()},
        )

    def toggle_side_panel(self):
        self.side_panel_visible_var.set(not self.side_panel_visible_var.get())
        self._apply_panel_layout()
        self.status("Panelen ingeschoven." if not self.side_panel_visible_var.get() else "Panelen uitgeschoven.")

    def set_side_panel_visible(self, visible: bool):
        self.side_panel_visible_var.set(bool(visible))
        self._apply_panel_layout()
        self.status("Panelen zichtbaar." if visible else "Panelen verborgen.")

    def set_panel_visible(self, key: str, visible: bool):
        if key not in self.panel_visible_vars:
            return
        self.panel_visible_vars[key].set(bool(visible))
        if visible:
            self.side_panel_visible_var.set(True)
        self._apply_panel_layout()

    def toggle_panel_collapsed(self, key: str):
        if key not in self.panel_collapsed_vars:
            return
        self.panel_collapsed_vars[key].set(not self.panel_collapsed_vars[key].get())
        self._apply_panel_layout()

    def set_panel_collapsed(self, key: str, collapsed: bool):
        if key not in self.panel_collapsed_vars:
            return
        self.panel_collapsed_vars[key].set(bool(collapsed))
        self._apply_panel_layout()

    def show_all_panels(self):
        self.side_panel_visible_var.set(True)
        for var in self.panel_visible_vars.values():
            var.set(True)
        self._apply_panel_layout()
        self.status("Alle panelen zichtbaar.")

    def expand_all_panels(self):
        self.side_panel_visible_var.set(True)
        for var in self.panel_visible_vars.values():
            var.set(True)
        for var in self.panel_collapsed_vars.values():
            var.set(False)
        self._apply_panel_layout()
        self.status("Alle panelen uitgeklapt.")

    def collapse_all_panels(self):
        self.side_panel_visible_var.set(True)
        for var in self.panel_collapsed_vars.values():
            var.set(True)
        self._apply_panel_layout()
        self.status("Alle panelen ingeklapt.")

    def _register_wrap_label(self, label, extra: int = 24):
        """Laat een label meebuigen met de paneelbreedte i.p.v. af te kappen.

        ``extra`` is de ruimte (px) die naast het label in dezelfde rij staat
        (bijv. knoppen of een naamkolom), zodat de afbreeklengte klopt.
        """
        if not hasattr(self, "_wrap_labels"):
            self._wrap_labels = []
        self._wrap_labels.append((label, extra))
        try:
            label.configure(wraplength=max(120, self._side_panel_width() - extra))
        except tk.TclError:
            pass

    def _update_wrap_labels(self, width):
        for label, extra in getattr(self, "_wrap_labels", ()):
            try:
                label.configure(wraplength=max(120, int(width) - extra))
            except tk.TclError:
                pass

    def _panel_note(self, parent, text: str, row: int, columnspan: int = 1):
        label = ttk.Label(parent, text=text, style="Subtle.TLabel", justify="left")
        label.grid(row=row, column=0, columnspan=columnspan, sticky="ew", pady=(0, 6))
        self._register_wrap_label(label, extra=36)
        return label

    def _make_mode_button(self, parent, mode: str, text: str, row: int, column: int, **grid_options):
        button = ttk.Button(parent, text=text, style="Tool.TButton", command=lambda m=mode: self.set_mode(m))
        button.grid(row=row, column=column, sticky=grid_options.pop("sticky", "ew"), **grid_options)
        self._mode_buttons.setdefault(mode, []).append(button)
        return button

    def _update_mode_buttons(self):
        if not getattr(self, "_mode_buttons", None):
            return
        labels = {
            "select": "Selecteer",
            "draw_wire": "Draad",
            "draw_leader": "Leader",
            "draw_dimension": "Maatlijn",
            "place_connector": "Connector",
            "draw_table": "Tabel",
            "place_node": "Knoop",
        }
        # De actieve tekenmodus krijgt het gevulde klei-accent; de tekst-prefix
        # is niet meer nodig omdat de kleur het onderscheid al maakt.
        active_style = "Accent.TButton"
        use_prefix = False
        for mode, buttons in self._mode_buttons.items():
            label = labels.get(mode, mode)
            is_active = mode == self.mode
            for button in buttons:
                try:
                    text = f"> {label}" if (is_active and use_prefix) else label
                    button.configure(text=text, style=active_style if is_active else "Tool.TButton")
                except tk.TclError:
                    continue

    def _build_ui(self):
        self._configure_app_style()
        self.columnconfigure(0, minsize=self._side_panel_width())
        self.columnconfigure(1, minsize=6)
        self.columnconfigure(2, weight=1)
        self.rowconfigure(1, weight=1)

        left = ttk.Frame(self, padding=8)
        self.left_sidebar = left
        left.grid(row=0, column=0, rowspan=3, sticky="nsew")
        left.grid_propagate(False)
        left.columnconfigure(0, weight=1)
        left.rowconfigure(1, weight=1)

        side_header = ttk.Frame(left)
        side_header.grid(row=0, column=0, sticky="ew", pady=(0, 8))
        side_header.columnconfigure(0, weight=1)
        # Wordmerk op een eigen regel (volle breedte) zodat het nooit afkapt;
        # de bedieningsknoppen staan rechts op de regel eronder.
        wordmark = ttk.Label(side_header, text="Kabelboom studio", style="Status.TLabel", anchor="w")
        wordmark.grid(row=0, column=0, columnspan=3, sticky="ew", pady=(0, 4))
        self._register_wrap_label(wordmark, extra=12)
        expand_all_btn = ttk.Button(side_header, text="Alles", width=6, command=self.expand_all_panels)
        expand_all_btn.grid(row=1, column=1, sticky="e", padx=(4, 0))
        attach_tooltip(expand_all_btn, "Alle panelen uitklappen")
        hide_side_btn = ttk.Button(side_header, text="<<", width=3, command=lambda: self.set_side_panel_visible(False))
        hide_side_btn.grid(row=1, column=2, sticky="e", padx=(4, 0))
        attach_tooltip(hide_side_btn, "Zijpaneel verbergen")
        left.bind("<Button-3>", self._show_panel_context_menu, add="+")
        side_header.bind("<Button-3>", self._show_panel_context_menu, add="+")

        left_scroll_wrap = ttk.Frame(left)
        left_scroll_wrap.grid(row=1, column=0, sticky="nsew")
        left_scroll_wrap.columnconfigure(0, weight=1)
        left_scroll_wrap.rowconfigure(0, weight=1)

        sidebar_bg = ttk.Style().lookup("TFrame", "background") or self.cget("bg")
        self.left_panel_canvas = tk.Canvas(left_scroll_wrap, background=sidebar_bg, highlightthickness=0, borderwidth=0)
        self.left_panel_canvas.grid(row=0, column=0, sticky="nsew")
        self.left_panel_scrollbar = ttk.Scrollbar(left_scroll_wrap, orient="vertical", command=self.left_panel_canvas.yview)
        self.left_panel_scrollbar.grid(row=0, column=1, sticky="ns")
        self.left_panel_canvas.configure(yscrollcommand=self.left_panel_scrollbar.set)
        self.left_panel = ttk.Frame(self.left_panel_canvas)
        self.left_panel.columnconfigure(0, weight=1)
        self.left_panel_window = self.left_panel_canvas.create_window((0, 0), window=self.left_panel, anchor="nw")
        self.left_panel.bind("<Configure>", self._update_left_panel_scrollregion)
        self.left_panel_canvas.bind("<Configure>", self._on_left_panel_canvas_configure)
        self.left_panel_canvas.bind("<MouseWheel>", self._on_left_panel_mousewheel, add="+")
        self.left_panel_canvas.bind("<Button-4>", self._on_left_panel_mousewheel, add="+")
        self.left_panel_canvas.bind("<Button-5>", self._on_left_panel_mousewheel, add="+")
        self.left_panel_canvas.bind("<Button-3>", self._show_panel_context_menu, add="+")

        symbol_body = self._create_left_panel_section("symbols", self.left_panel, 2)
        symbol_wrap = ttk.Frame(symbol_body)
        symbol_wrap.grid(row=0, column=0, sticky="nsew")
        symbol_wrap.columnconfigure(0, weight=1)
        symbol_wrap.rowconfigure(0, weight=1)
        self.symbol_list = tk.Listbox(symbol_wrap, height=7, exportselection=False)
        self.symbol_list.grid(row=0, column=0, sticky="nsew")
        self.symbol_list.bind("<<ListboxSelect>>", self._on_symbol_select)
        self.symbol_list.bind("<Double-Button-1>", self._on_symbol_activate)
        self.symbol_list.bind("<Return>", self._on_symbol_activate)
        self.symbol_list.bind("<Button-3>", self._show_symbol_context_menu)
        self.symbol_list_scroll = ttk.Scrollbar(symbol_wrap, orient="vertical", command=self.symbol_list.yview)
        self.symbol_list_scroll.grid(row=0, column=1, sticky="ns")
        self.symbol_list.configure(yscrollcommand=self.symbol_list_scroll.set)
        place_symbol_btn = ttk.Button(symbol_body, text="Plaats geselecteerde connector", style="Tool.TButton", command=self._place_selected_symbol)
        place_symbol_btn.grid(row=1, column=0, sticky="ew", pady=(4, 0))
        attach_tooltip(place_symbol_btn, "Start plaatsmodus voor de gekozen connector uit de bibliotheek (of dubbelklik in de lijst)")

        blocks_frame = ttk.Frame(symbol_body)
        blocks_frame.grid(row=2, column=0, sticky="ew", pady=(8, 0))
        blocks_frame.columnconfigure(0, weight=1)
        ttk.Label(blocks_frame, text="Herbruikbare blokken", style="Subtle.TLabel").grid(row=0, column=0, columnspan=2, sticky="w")
        self.block_name_var = tk.StringVar(value="")
        self.block_combo = ttk.Combobox(blocks_frame, textvariable=self.block_name_var, values=[], state="readonly")
        self.block_combo.grid(row=1, column=0, columnspan=2, sticky="ew", pady=(2, 2))
        block_btns = ttk.Frame(blocks_frame)
        block_btns.grid(row=2, column=0, columnspan=2, sticky="ew")
        block_btns.columnconfigure(0, weight=1)
        block_btns.columnconfigure(1, weight=1)
        ttk.Button(block_btns, text="Selectie → blok", style="Tool.TButton", command=self.save_selection_as_block).grid(row=0, column=0, sticky="ew", padx=(0, 3), pady=1)
        ttk.Button(block_btns, text="Invoegen", style="Tool.TButton", command=self.insert_selected_block).grid(row=0, column=1, sticky="ew", padx=(3, 0), pady=1)
        ttk.Button(block_btns, text="Blok verwijderen", style="Tool.TButton", command=self.delete_selected_block).grid(row=1, column=0, columnspan=2, sticky="ew", pady=1)

        tools_body = self._create_left_panel_section("tools", self.left_panel, 0)
        button_col = ttk.Frame(tools_body)
        button_col.grid(row=0, column=0, sticky="ew")
        button_col.columnconfigure(0, weight=1)
        button_col.columnconfigure(1, weight=1)

        self._panel_note(button_col, "Kies een tekenmodus in de werkbalk bovenaan. Hieronder staan invoeg- en bewerkacties.", 0, columnspan=2)
        step_btn = ttk.Button(button_col, text="STEP import", style="Tool.TButton", command=self.import_step_symbol)
        step_btn.grid(row=1, column=0, sticky="ew", padx=(0, 3), pady=2)
        attach_tooltip(step_btn, "Connector uit STEP-bestand importeren (met 3D-preview en projectiezijde)")
        ttk.Button(button_col, text="Plakken", style="Tool.TButton", command=self.paste_from_clipboard).grid(row=1, column=1, sticky="ew", padx=(3, 0), pady=2)
        ttk.Button(button_col, text="Afbeelding", style="Tool.TButton", command=self.import_image_note).grid(row=2, column=0, sticky="ew", padx=(0, 3), pady=2)
        ttk.Button(button_col, text="Eigenschappen", style="Tool.TButton", command=self.focus_properties_panel).grid(row=2, column=1, sticky="ew", padx=(3, 0), pady=2)
        ttk.Button(button_col, text="Verwijder", style="Tool.TButton", command=self.delete_selected).grid(row=3, column=0, columnspan=2, sticky="ew", pady=2)

        project_body = self._create_left_panel_section("project", self.left_panel, 3)
        meta = ttk.Frame(project_body)
        meta.grid(row=0, column=0, sticky="ew")
        meta.columnconfigure(1, weight=1)

        ttk.Label(meta, text="Naam").grid(row=0, column=0, sticky="w")
        ttk.Entry(meta, textvariable=self.project_name_var).grid(row=0, column=1, sticky="ew", padx=(4, 0))
        ttk.Label(meta, text="Rev").grid(row=1, column=0, sticky="w")
        ttk.Entry(meta, textvariable=self.rev_var).grid(row=1, column=1, sticky="ew", padx=(4, 0))
        ttk.Label(meta, text="Engineer").grid(row=2, column=0, sticky="w")
        ttk.Entry(meta, textvariable=self.engineer_var).grid(row=2, column=1, sticky="ew", padx=(4, 0))
        ttk.Label(meta, text="IEC blad").grid(row=3, column=0, sticky="w")
        self.paper_preset_combo = ttk.Combobox(meta, textvariable=self.paper_preset_var, values=PAPER_PRESET_OPTIONS, state="readonly")
        self.paper_preset_combo.grid(row=3, column=1, sticky="ew", padx=(4, 0))
        self.paper_preset_combo.bind("<<ComboboxSelected>>", self._on_paper_preset_changed)
        ttk.Separator(meta).grid(row=4, column=0, columnspan=2, sticky="ew", pady=(8, 4))
        ttk.Label(meta, text="Kruisingen", style="Subtle.TLabel").grid(row=5, column=0, columnspan=2, sticky="w")
        self.wire_bridge_enabled_check = ttk.Checkbutton(
            meta,
            text="Boogjes tonen",
            variable=self.wire_bridge_enabled_var,
            command=self.apply_wire_bridge_settings,
        )
        self.wire_bridge_enabled_check.grid(row=6, column=1, sticky="w", padx=(4, 0))
        ttk.Label(meta, text="Booghoogte mm").grid(row=7, column=0, sticky="w")
        self.wire_bridge_height_entry = ttk.Entry(meta, textvariable=self.wire_bridge_height_var)
        self.wire_bridge_height_entry.grid(row=7, column=1, sticky="ew", padx=(4, 0))
        ttk.Label(meta, text="Booglengte mm").grid(row=8, column=0, sticky="w")
        self.wire_bridge_length_entry = ttk.Entry(meta, textvariable=self.wire_bridge_length_var)
        self.wire_bridge_length_entry.grid(row=8, column=1, sticky="ew", padx=(4, 0))
        ttk.Label(meta, text="Vrijruimte mm").grid(row=9, column=0, sticky="w")
        self.wire_bridge_clearance_entry = ttk.Entry(meta, textvariable=self.wire_bridge_clearance_var)
        self.wire_bridge_clearance_entry.grid(row=9, column=1, sticky="ew", padx=(4, 0))
        bridge_actions = ttk.Frame(meta)
        bridge_actions.grid(row=10, column=0, columnspan=2, sticky="ew", pady=(4, 0))
        bridge_actions.columnconfigure(0, weight=1)
        bridge_actions.columnconfigure(1, weight=1)
        ttk.Button(bridge_actions, text="Toepassen", command=self.apply_wire_bridge_settings).grid(
            row=0, column=0, sticky="ew", padx=(0, 3)
        )
        ttk.Button(bridge_actions, text="Reset", command=self.reset_wire_bridge_settings).grid(
            row=0, column=1, sticky="ew", padx=(3, 0)
        )
        for entry in (self.wire_bridge_height_entry, self.wire_bridge_length_entry, self.wire_bridge_clearance_entry):
            entry.bind("<Return>", lambda _event: self.apply_wire_bridge_settings())
            entry.bind("<FocusOut>", lambda _event: self.apply_wire_bridge_settings())

        tb = ttk.Frame(project_body)
        tb.grid(row=1, column=0, sticky="ew", pady=(8, 0))
        tb.columnconfigure(1, weight=1)
        tb.columnconfigure(3, weight=1)
        ttk.Label(tb, text="Titelblok", style="Subtle.TLabel").grid(row=0, column=0, columnspan=4, sticky="w")
        tb_fields = [
            ("DWG nr", self.drawing_number_var, 1, 0),
            ("Klant", self.customer_var, 1, 2),
            ("Getekend dat.", self.date_drawn_var, 2, 0),
            ("Schaal", self.scale_text_var, 2, 2),
            ("Gecontr.", self.checked_by_var, 3, 0),
            ("Datum", self.date_checked_var, 3, 2),
            ("Goedgek.", self.approved_by_var, 4, 0),
            ("Datum", self.date_approved_var, 4, 2),
            ("Blad", self.sheet_var, 5, 0),
            ("Eenheid", self.unit_var, 5, 2),
        ]
        for label, var, r, c in tb_fields:
            ttk.Label(tb, text=label).grid(row=r, column=c, sticky="w")
            ttk.Entry(tb, textvariable=var, width=10).grid(row=r, column=c + 1, sticky="ew", padx=(4, 6))
        ttk.Label(tb, text="Tol X/XX/XXX").grid(row=6, column=0, sticky="w")
        tol_row = ttk.Frame(tb)
        tol_row.grid(row=6, column=1, columnspan=3, sticky="ew", padx=(4, 0))
        for idx, var in enumerate((self.tol_x_var, self.tol_xx_var, self.tol_xxx_var)):
            tol_row.columnconfigure(idx, weight=1)
            ttk.Entry(tol_row, textvariable=var, width=6).grid(row=0, column=idx, sticky="ew", padx=(0, 3))
        for _var in (
            self.project_name_var, self.rev_var, self.engineer_var, self.drawing_number_var,
            self.customer_var, self.checked_by_var, self.approved_by_var, self.date_drawn_var,
            self.date_checked_var, self.date_approved_var, self.scale_text_var, self.sheet_var,
            self.unit_var, self.tol_x_var, self.tol_xx_var, self.tol_xxx_var,
        ):
            _var.trace_add("write", lambda *_a: self.request_scene_redraw())

        files_body = self._create_left_panel_section("files", self.left_panel, 4)
        io = ttk.Frame(files_body)
        io.grid(row=0, column=0, sticky="ew")
        io.columnconfigure(0, weight=1)
        io.columnconfigure(1, weight=1)
        ttk.Button(io, text="Nieuw", style="Tool.TButton", command=self.new_project).grid(row=0, column=0, sticky="ew", padx=(0, 3), pady=2)
        ttk.Button(io, text="Open", style="Tool.TButton", command=self.open_project).grid(row=0, column=1, sticky="ew", padx=(3, 0), pady=2)
        ttk.Button(io, text="Opslaan", style="Tool.TButton", command=self.save_project).grid(row=1, column=0, sticky="ew", padx=(0, 3), pady=2)
        ttk.Button(io, text="Opslaan als", style="Tool.TButton", command=self.save_project_as).grid(row=1, column=1, sticky="ew", padx=(3, 0), pady=2)
        export_buttons = [
            ("SVG", self.export_svg, "Exporteer de tekening als SVG (vectorformaat)", 2, 0, (8, 2)),
            ("PNG", self.export_png, "Exporteer de tekening als PNG-afbeelding", 2, 1, (8, 2)),
            ("PDF", self.export_pdf, "Exporteer de tekening als PDF", 3, 0, 2),
            ("Controle", self.run_project_check, "Projectcontrole (DRC): controleer draden tegen connector-pinnen", 3, 1, 2),
            ("Netlist CSV", self.export_netlist_csv, "Exporteer netlist (draadverbindingen) als CSV", 4, 0, 2),
            ("BOM CSV", self.export_bom_csv, "Exporteer stuklijst (BOM) als CSV", 4, 1, 2),
        ]
        for text, command, tip, r, c, pady in export_buttons:
            padx = (0, 3) if c == 0 else (3, 0)
            btn = ttk.Button(io, text=text, style="Tool.TButton", command=command)
            btn.grid(row=r, column=c, sticky="ew", padx=padx, pady=pady)
            attach_tooltip(btn, tip)

        properties_body = self._create_left_panel_section("properties", self.left_panel, 1)
        self.property_hint_var = tk.StringVar(value="Selecteer iets of kies een tekenmodus; alleen relevante velden blijven zichtbaar.")
        property_hint_lbl = ttk.Label(properties_body, textvariable=self.property_hint_var, style="Subtle.TLabel", justify="left")
        property_hint_lbl.grid(row=0, column=0, sticky="ew", pady=(0, 6))
        self._register_wrap_label(property_hint_lbl, extra=28)
        props = ttk.Frame(properties_body)
        self.properties_frame = props
        props.grid(row=1, column=0, sticky="ew")
        props.columnconfigure(1, weight=1)

        self.prop_target_var = tk.StringVar(value="Geen selectie")
        self.prop_color_var = tk.StringVar(value="")
        self.prop_color_b_var = tk.StringVar(value="")
        self.prop_width_var = tk.StringVar(value="")
        self.prop_text_var = tk.StringVar(value="")
        self.prop_scale_var = tk.StringVar(value="")
        self.prop_wire_style_var = tk.StringVar(value="Recht")
        self.prop_curve_var = tk.StringVar(value="8")
        self.prop_twist_pitch_var = tk.StringVar(value="10")
        self.prop_pair_gap_var = tk.StringVar(value="2.8")
        self.prop_signal_var = tk.StringVar(value="")
        self.prop_from_connector_var = tk.StringVar(value="")
        self.prop_from_pin_var = tk.StringVar(value="")
        self.prop_to_connector_var = tk.StringVar(value="")
        self.prop_to_pin_var = tk.StringVar(value="")
        self.prop_cross_section_var = tk.StringVar(value="0.35")
        self.prop_length_var = tk.StringVar(value="0")
        self.prop_net_var = tk.StringVar(value="")
        self.prop_shielded_var = tk.BooleanVar(value=False)
        self.prop_connector_id_var = tk.StringVar(value="")
        self.prop_connector_part_var = tk.StringVar(value="")
        self.prop_connector_pin_count_var = tk.StringVar(value="1")
        self.prop_connector_pin_labels_var = tk.StringVar(value="")
        self.prop_connector_label_dx_var = tk.StringVar(value="0")
        self.prop_connector_label_dy_var = tk.StringVar(value="-6")
        self.prop_leader_text_size_var = tk.StringVar(value=f"{DEFAULT_LEADER_TEXT_SIZE_PT:g}")
        self.prop_leader_text_box_var = tk.BooleanVar(value=False)
        self.prop_dim_orientation_var = tk.StringVar(value=dimension_orientation_label("horizontal"))
        self.prop_dim_offset_var = tk.StringVar(value=f"{DEFAULT_DIMENSION_OFFSET_MM:g}")
        self.prop_dim_tolerance_var = tk.StringVar(value="")
        self.prop_color_var.trace_add("write", lambda *_: self._update_color_preview())
        self.prop_color_b_var.trace_add("write", lambda *_: self._update_color_b_preview())
        self.prop_wire_style_var.trace_add("write", lambda *_: self._on_wire_style_property_changed())
        self.prop_wire_move_scope_var.trace_add("write", lambda *_: self._on_wire_move_scope_changed())
        self.prop_wire_endpoint_drag_scope_var.trace_add("write", lambda *_: self._on_wire_endpoint_drag_scope_changed())

        # --- Eigenschappen gegroepeerd in LabelFrame-blokken (geen harde rijnummers
        # meer in de UI). Velden houden intern een stabiele veld-id; blokken
        # klappen automatisch in als er voor de huidige selectie niets in staat. ---
        self._field_widgets = {}
        self._field_block = {}
        self._property_blocks = []
        self._property_block_rows = {}

        def make_block(title):
            frame = ttk.LabelFrame(props, text=title, padding=(8, 4))
            frame.columnconfigure(1, weight=1)
            frame.grid(row=len(self._property_blocks) + 1, column=0, columnspan=2, sticky="ew", pady=(6, 0))
            self._property_blocks.append(frame)
            self._property_block_rows[frame] = 0
            return frame

        def add_field(block, field_id, label_text, widget, label_widget=None):
            r = self._property_block_rows[block]
            widgets = []
            if label_text is not None:
                lbl = label_widget if label_widget is not None else ttk.Label(block, text=label_text)
                lbl.grid(in_=block, row=r, column=0, sticky="w", pady=1)
                widgets.append(lbl)
            widget.grid(in_=block, row=r, column=1, sticky="ew", padx=(4, 0), pady=1)
            widgets.append(widget)
            self._property_block_rows[block] = r + 1
            self._field_widgets[field_id] = widgets
            self._field_block[field_id] = block

        def add_span(block, field_id, widget):
            r = self._property_block_rows[block]
            widget.grid(in_=block, row=r, column=0, columnspan=2, sticky="ew", pady=(4, 0))
            self._property_block_rows[block] = r + 1
            self._field_widgets[field_id] = [widget]
            self._field_block[field_id] = block

        # Objectkop (altijd zichtbaar): naam-label of bewerkbaar connector-ID-veld.
        ttk.Label(props, text="Object").grid(row=0, column=0, sticky="w")
        self.prop_target_lbl = ttk.Label(props, textvariable=self.prop_target_var, justify="left")
        self.prop_target_lbl.grid(row=0, column=1, sticky="ew")
        self._register_wrap_label(self.prop_target_lbl, extra=70)
        self.prop_connector_id_entry = ttk.Entry(props, textvariable=self.prop_connector_id_var)
        self.prop_connector_id_entry.grid(row=0, column=1, sticky="ew", padx=(4, 0))
        self.prop_connector_id_entry.bind("<Return>", lambda _e: self._rename_selected_connector())
        self.prop_connector_id_entry.bind("<FocusOut>", lambda _e: self._rename_selected_connector())
        self.prop_connector_id_entry.grid_remove()

        # Blok: Stijl
        style_block = make_block("Stijl")
        color_row = ttk.Frame(style_block)
        color_row.columnconfigure(0, weight=1)
        self.prop_color_entry = ttk.Entry(color_row, textvariable=self.prop_color_var)
        self.prop_color_entry.grid(row=0, column=0, sticky="ew")
        self.prop_color_pick_btn = ttk.Button(color_row, text="Kies", command=self.pick_color_for_panel, width=6)
        self.prop_color_pick_btn.grid(row=0, column=1, padx=(4, 0))
        self.prop_color_preview = tk.Label(color_row, width=2, relief="sunken", bg="#1f4e79")
        self.prop_color_preview.grid(row=0, column=2, padx=(4, 0))
        add_field(style_block, 1, "Kleur", color_row)
        color_b_row = ttk.Frame(style_block)
        color_b_row.columnconfigure(0, weight=1)
        self.prop_color_b_entry = ttk.Entry(color_b_row, textvariable=self.prop_color_b_var)
        self.prop_color_b_entry.grid(row=0, column=0, sticky="ew")
        self.prop_color_b_pick_btn = ttk.Button(color_b_row, text="Kies", command=self.pick_color_b_for_panel, width=6)
        self.prop_color_b_pick_btn.grid(row=0, column=1, padx=(4, 0))
        self.prop_color_b_preview = tk.Label(color_b_row, width=2, relief="sunken", bg="#d7263d")
        self.prop_color_b_preview.grid(row=0, column=2, padx=(4, 0))
        add_field(style_block, 7, "Kleur B", color_b_row)
        self.prop_width_entry = ttk.Entry(style_block, textvariable=self.prop_width_var)
        add_field(style_block, 2, "Lijndikte mm", self.prop_width_entry)
        palette_row = ttk.Frame(style_block)
        palette_colors = ui_theme.DRAWING_PALETTE
        self.prop_palette_buttons: List[tk.Button] = []
        for idx, color in enumerate(palette_colors):
            btn = tk.Button(
                palette_row,
                bg=color,
                activebackground=color,
                width=2,
                relief="flat",
                command=lambda c=color: self.set_panel_color(c),
            )
            btn.grid(row=0, column=idx, padx=1)
            self.prop_palette_buttons.append(btn)
        add_span(style_block, 21, palette_row)

        # Blok: Tekst
        text_block = make_block("Tekst")
        self.prop_text_label = ttk.Label(text_block, text="Tekst / label")
        self.prop_text_entry = ttk.Entry(text_block, textvariable=self.prop_text_var)
        add_field(text_block, 3, "Tekst / label", self.prop_text_entry, label_widget=self.prop_text_label)
        self.prop_leader_text_size_entry = ttk.Entry(text_block, textvariable=self.prop_leader_text_size_var)
        add_field(text_block, 23, "Tekstgrootte pt", self.prop_leader_text_size_entry)
        self.prop_leader_text_box_check = ttk.Checkbutton(text_block, text="Tekstkader tonen", variable=self.prop_leader_text_box_var)
        add_field(text_block, 24, None, self.prop_leader_text_box_check)

        # Blok: Geometrie
        geom_block = make_block("Geometrie")
        self.prop_scale_label = ttk.Label(geom_block, text="Schaal")
        self.prop_scale_entry = ttk.Entry(geom_block, textvariable=self.prop_scale_var)
        add_field(geom_block, 4, "Schaal", self.prop_scale_entry, label_widget=self.prop_scale_label)

        # Blok: Draad
        wire_block = make_block("Draad")
        self.prop_wire_style_combo = ttk.Combobox(wire_block, textvariable=self.prop_wire_style_var, values=WIRE_STYLE_OPTIONS, state="readonly")
        add_field(wire_block, 5, "Draadtype", self.prop_wire_style_combo)
        self.prop_curve_entry = ttk.Entry(wire_block, textvariable=self.prop_curve_var)
        add_field(wire_block, 6, "Bocht mm", self.prop_curve_entry)
        self.prop_twist_pitch_entry = ttk.Entry(wire_block, textvariable=self.prop_twist_pitch_var)
        add_field(wire_block, 8, "Twist pitch mm", self.prop_twist_pitch_entry)
        self.prop_pair_gap_entry = ttk.Entry(wire_block, textvariable=self.prop_pair_gap_var)
        add_field(wire_block, 9, "Pair gap mm", self.prop_pair_gap_entry)
        self.prop_wire_move_scope_combo = ttk.Combobox(
            wire_block, textvariable=self.prop_wire_move_scope_var, values=WIRE_MOVE_SCOPE_OPTIONS, state="readonly"
        )
        add_field(wire_block, 19, "Verplaats draad", self.prop_wire_move_scope_combo)
        self.prop_wire_endpoint_drag_scope_combo = ttk.Combobox(
            wire_block, textvariable=self.prop_wire_endpoint_drag_scope_var, values=WIRE_ENDPOINT_DRAG_SCOPE_OPTIONS, state="readonly"
        )
        add_field(wire_block, 20, "Sleep eindpunt", self.prop_wire_endpoint_drag_scope_combo)

        # Blok: Elektrisch
        elec_block = make_block("Elektrisch")
        self.prop_signal_entry = ttk.Entry(elec_block, textvariable=self.prop_signal_var)
        add_field(elec_block, 10, "Signaal", self.prop_signal_entry)
        from_row = ttk.Frame(elec_block)
        from_row.columnconfigure(0, weight=1)
        from_row.columnconfigure(1, weight=1)
        self.prop_from_connector_entry = ttk.Entry(from_row, textvariable=self.prop_from_connector_var)
        self.prop_from_connector_entry.grid(row=0, column=0, sticky="ew", padx=(0, 3))
        self.prop_from_pin_entry = ttk.Entry(from_row, textvariable=self.prop_from_pin_var)
        self.prop_from_pin_entry.grid(row=0, column=1, sticky="ew", padx=(3, 0))
        add_field(elec_block, 11, "Van conn/pin", from_row)
        to_row = ttk.Frame(elec_block)
        to_row.columnconfigure(0, weight=1)
        to_row.columnconfigure(1, weight=1)
        self.prop_to_connector_entry = ttk.Entry(to_row, textvariable=self.prop_to_connector_var)
        self.prop_to_connector_entry.grid(row=0, column=0, sticky="ew", padx=(0, 3))
        self.prop_to_pin_entry = ttk.Entry(to_row, textvariable=self.prop_to_pin_var)
        self.prop_to_pin_entry.grid(row=0, column=1, sticky="ew", padx=(3, 0))
        add_field(elec_block, 12, "Naar conn/pin", to_row)
        wire_dims_row = ttk.Frame(elec_block)
        wire_dims_row.columnconfigure(0, weight=1)
        wire_dims_row.columnconfigure(1, weight=1)
        self.prop_cross_section_entry = ttk.Entry(wire_dims_row, textvariable=self.prop_cross_section_var)
        self.prop_cross_section_entry.grid(row=0, column=0, sticky="ew", padx=(0, 3))
        self.prop_length_entry = ttk.Entry(wire_dims_row, textvariable=self.prop_length_var)
        self.prop_length_entry.grid(row=0, column=1, sticky="ew", padx=(3, 0))
        add_field(elec_block, 13, "mm2 / lengte", wire_dims_row)
        self.prop_net_entry = ttk.Entry(elec_block, textvariable=self.prop_net_var)
        add_field(elec_block, 14, "Net", self.prop_net_entry)
        self.prop_shielded_check = ttk.Checkbutton(elec_block, text="Shielded", variable=self.prop_shielded_var)
        add_field(elec_block, 15, None, self.prop_shielded_check)

        # Blok: Connector
        conn_block = make_block("Connector")
        self.prop_connector_part_entry = ttk.Entry(conn_block, textvariable=self.prop_connector_part_var)
        add_field(conn_block, 16, "Connector part", self.prop_connector_part_entry)
        self.prop_connector_pin_count_entry = ttk.Entry(conn_block, textvariable=self.prop_connector_pin_count_var)
        add_field(conn_block, 17, "Connector pins", self.prop_connector_pin_count_entry)
        self.prop_connector_pin_labels_entry = ttk.Entry(conn_block, textvariable=self.prop_connector_pin_labels_var)
        add_field(conn_block, 18, "Pinlabels", self.prop_connector_pin_labels_entry)
        self.prop_connector_label_offset_label = ttk.Label(conn_block, text="Naam offset mm")
        label_offset_row = ttk.Frame(conn_block)
        label_offset_row.columnconfigure(0, weight=1)
        label_offset_row.columnconfigure(1, weight=1)
        self.prop_connector_label_dx_entry = ttk.Entry(label_offset_row, textvariable=self.prop_connector_label_dx_var)
        self.prop_connector_label_dx_entry.grid(row=0, column=0, sticky="ew", padx=(0, 3))
        self.prop_connector_label_dy_entry = ttk.Entry(label_offset_row, textvariable=self.prop_connector_label_dy_var)
        self.prop_connector_label_dy_entry.grid(row=0, column=1, sticky="ew", padx=(3, 0))
        add_field(conn_block, 28, "Naam offset mm", label_offset_row, label_widget=self.prop_connector_label_offset_label)

        # Blok: Maatlijn
        dim_block = make_block("Maatlijn")
        self.prop_dim_orientation_label = ttk.Label(dim_block, text="Maatrichting")
        self.prop_dim_orientation_combo = ttk.Combobox(
            dim_block, textvariable=self.prop_dim_orientation_var, values=DIMENSION_ORIENTATION_OPTIONS, state="readonly"
        )
        self.prop_dim_orientation_combo.bind("<<ComboboxSelected>>", lambda _e: self._on_dimension_quick_edit())
        add_field(dim_block, 25, "Maatrichting", self.prop_dim_orientation_combo, label_widget=self.prop_dim_orientation_label)
        self.prop_dim_offset_label = ttk.Label(dim_block, text="Offset mm")
        self.prop_dim_offset_entry = ttk.Entry(dim_block, textvariable=self.prop_dim_offset_var)
        add_field(dim_block, 26, "Offset mm", self.prop_dim_offset_entry, label_widget=self.prop_dim_offset_label)
        self.prop_dim_tolerance_label = ttk.Label(dim_block, text="Tolerantie")
        self.prop_dim_tolerance_entry = ttk.Entry(dim_block, textvariable=self.prop_dim_tolerance_var)
        add_field(dim_block, 27, "Tolerantie", self.prop_dim_tolerance_entry, label_widget=self.prop_dim_tolerance_label)

        # Acties (altijd zichtbaar onderaan).
        # Gestapeld op volle breedte: lange labels worden nooit afgekapt,
        # ook niet bij een smal paneel of lagere UI-schaal.
        actions = ttk.Frame(props)
        actions.grid(row=len(self._property_blocks) + 2, column=0, columnspan=2, sticky="ew", pady=(10, 0))
        actions.columnconfigure(0, weight=1)
        self.prop_apply_btn = ttk.Button(actions, text="Toepassen op selectie", style="Tool.TButton", command=self.apply_properties_from_panel)
        self.prop_apply_btn.grid(row=0, column=0, sticky="ew", pady=(0, 4))
        self.prop_default_btn = ttk.Button(actions, text="Maak default stijl", style="Tool.TButton", command=self.apply_defaults_from_panel)
        self.prop_default_btn.grid(row=1, column=0, sticky="ew")
        self._capture_property_row_widgets()
        self._bind_left_panel_scroll_handlers(self.left_panel)
        self._bind_panel_context_handlers(self.left_panel)
        self.after_idle(self._update_left_panel_scrollregion)

        self.side_panel_resize_grip = tk.Frame(self, width=6, background=ui_theme.color(self._app_theme, "grip"), cursor="sb_h_double_arrow", bd=0)
        self.side_panel_resize_grip.grid(row=0, column=1, rowspan=3, sticky="ns")
        self.side_panel_resize_grip.bind("<Button-1>", self._on_side_panel_resize_down)
        self.side_panel_resize_grip.bind("<B1-Motion>", self._on_side_panel_resize_drag)
        self.side_panel_resize_grip.bind("<ButtonRelease-1>", self._on_side_panel_resize_up)

        top = ttk.Frame(self, padding=(8, 8, 8, 0))
        top.grid(row=0, column=2, sticky="ew")
        top.columnconfigure(13, weight=1)
        ttk.Button(top, textvariable=self.side_panel_toggle_text_var, command=self.toggle_side_panel).grid(row=0, column=0, sticky="w", padx=(0, 8))
        ttk.Label(top, text="Workflow", style="Subtle.TLabel").grid(row=0, column=1, sticky="w", padx=(0, 4))
        self._make_mode_button(top, "select", "Selecteer", 0, 2, padx=2)
        self._make_mode_button(top, "draw_wire", "Draad", 0, 3, padx=2)
        self._make_mode_button(top, "draw_leader", "Leader", 0, 4, padx=2)
        self._make_mode_button(top, "draw_dimension", "Maatlijn", 0, 5, padx=2)
        self._make_mode_button(top, "place_connector", "Connector", 0, 6, padx=2)
        self._make_mode_button(top, "draw_table", "Tabel", 0, 7, padx=2)
        self._make_mode_button(top, "place_node", "Knoop", 0, 8, padx=2)
        fit_btn = ttk.Button(top, text="Fit", style="Tool.TButton", command=self.fit_page_to_view)
        fit_btn.grid(row=0, column=9, padx=(12, 2))
        attach_tooltip(fit_btn, "Blad passend in beeld (zoom-to-fit)")
        ttk.Button(top, text="Zoom -", style="Tool.TButton", command=lambda: self.zoom_by(1 / 1.15)).grid(row=0, column=10, padx=2)
        ttk.Button(top, text="Zoom +", style="Tool.TButton", command=lambda: self.zoom_by(1.15)).grid(row=0, column=11, padx=2)
        ttk.Label(top, textvariable=self.mode_var, style="Status.TLabel").grid(row=0, column=12, sticky="w", padx=(10, 0))

        snap_row = ttk.Frame(top)
        snap_row.grid(row=1, column=0, columnspan=14, sticky="ew", pady=(6, 0))
        snap_row.columnconfigure(8, weight=1)
        ttk.Button(snap_row, text="Undo", style="Tool.TButton", command=self.undo).grid(row=0, column=0, padx=(0, 3))
        ttk.Button(snap_row, text="Redo", style="Tool.TButton", command=self.redo).grid(row=0, column=1, padx=(0, 12))
        ttk.Checkbutton(snap_row, text="Grid snap", variable=self.snap_grid_enabled_var, command=self._on_snap_settings_changed).grid(row=0, column=2, padx=(0, 2))
        ttk.Label(snap_row, text="Stap mm").grid(row=0, column=3, padx=(6, 2))
        self.snap_grid_entry = ttk.Entry(snap_row, textvariable=self.snap_grid_mm_var, width=6)
        self.snap_grid_entry.grid(row=0, column=4, padx=(0, 8))
        self.snap_grid_entry.bind("<Return>", self._on_snap_settings_changed)
        ttk.Checkbutton(snap_row, text="Endpoint snap", variable=self.snap_endpoint_enabled_var, command=self._on_snap_settings_changed).grid(
            row=0, column=5, padx=(0, 12)
        )
        ttk.Label(
            snap_row,
            text="Shift/Ctrl selecteert door; rechtermuisknop toont contextopties; Esc keert terug naar selecteren.",
            style="Subtle.TLabel",
        ).grid(row=0, column=8, sticky="e")

        canvas_wrap = ttk.Frame(self, padding=8)
        canvas_wrap.grid(row=1, column=2, sticky="nsew")
        canvas_wrap.columnconfigure(0, weight=1)
        canvas_wrap.rowconfigure(0, weight=1)
        self.canvas = tk.Canvas(canvas_wrap, background=ui_theme.color(self._app_theme, "canvas_bg"), highlightthickness=0)
        self.canvas.grid(row=0, column=0, sticky="nsew")

        self._build_status_bar()
        self._update_mode_buttons()

    def _build_status_bar(self):
        status = ttk.Frame(self, padding=(8, 2, 8, 4))
        status.grid(row=2, column=2, sticky="ew")
        status.columnconfigure(0, weight=1)
        ttk.Label(status, textvariable=self.status_var, style="Subtle.TLabel", anchor="w").grid(row=0, column=0, sticky="ew")
        ttk.Label(status, textvariable=self.perf_var, style="Subtle.TLabel", anchor="e").grid(row=0, column=1, sticky="e", padx=(8, 0))
        ttk.Separator(status, orient="vertical").grid(row=0, column=2, sticky="ns", padx=8)
        ttk.Label(status, textvariable=self.coord_var, style="Subtle.TLabel", anchor="e", width=22).grid(row=0, column=3, sticky="e")
        help_btn = ttk.Button(status, text="? Sneltoetsen", style="Tool.TButton", command=self.show_shortcuts_dialog)
        help_btn.grid(row=0, column=4, sticky="e", padx=(8, 0))
        attach_tooltip(help_btn, "Toon alle sneltoetsen en muisbediening (F1)")

    def _populate_panel_menu(self, menu: tk.Menu):
        menu.add_checkbutton(
            label="Zijpaneel tonen",
            variable=self.side_panel_visible_var,
            command=lambda: self.set_side_panel_visible(self.side_panel_visible_var.get()),
        )
        menu.add_command(label="Zijpaneel in-/uitschuiven", command=self.toggle_side_panel)
        menu.add_separator()
        for key, label in LEFT_PANEL_DEFINITIONS:
            menu.add_checkbutton(
                label=f"Toon {label}",
                variable=self.panel_visible_vars[key],
                command=lambda k=key: self.set_panel_visible(k, self.panel_visible_vars[k].get()),
            )
        menu.add_separator()
        collapse_menu = tk.Menu(menu, tearoff=0)
        for key, label in LEFT_PANEL_DEFINITIONS:
            collapse_menu.add_checkbutton(
                label=label,
                variable=self.panel_collapsed_vars[key],
                command=lambda k=key: self.set_panel_collapsed(k, self.panel_collapsed_vars[k].get()),
            )
        menu.add_cascade(label="Ingeklapte panelen", menu=collapse_menu)
        menu.add_separator()
        menu.add_command(label="Alle panelen tonen", command=self.show_all_panels)
        menu.add_command(label="Alle panelen uitklappen", command=self.expand_all_panels)
        menu.add_command(label="Alle panelen inklappen", command=self.collapse_all_panels)

    def _add_panel_menu(self, parent_menu: tk.Menu, label: str = "Panelen"):
        panel_menu = tk.Menu(parent_menu, tearoff=0)
        self._populate_panel_menu(panel_menu)
        parent_menu.add_cascade(label=label, menu=panel_menu)

    def _add_theme_menu(self, parent_menu: tk.Menu, label: str = "Thema"):
        theme_menu = tk.Menu(parent_menu, tearoff=0)
        theme_menu.add_radiobutton(
            label="Licht", value="light", variable=self.app_theme_var, command=lambda: self.set_app_theme("light")
        )
        theme_menu.add_radiobutton(
            label="Donker", value="dark", variable=self.app_theme_var, command=lambda: self.set_app_theme("dark")
        )
        parent_menu.add_cascade(label=label, menu=theme_menu)

    def _add_ui_scale_menu(self, parent_menu: tk.Menu, label: str = "UI schaal"):
        ui_scale_menu = tk.Menu(parent_menu, tearoff=0)
        for scale_label in UI_SCALE_LABELS:
            ui_scale_menu.add_radiobutton(
                label=scale_label,
                value=scale_label,
                variable=self.ui_scale_var,
                command=self._on_ui_scale_changed,
            )
        parent_menu.add_cascade(label=label, menu=ui_scale_menu)

    def _add_application_context_submenus(self, menu: tk.Menu):
        menu.add_separator()

        project_menu = tk.Menu(menu, tearoff=0)
        project_menu.add_command(label="Nieuw project", command=self.new_project)
        project_menu.add_command(label="Open project...", command=self.open_project)
        project_menu.add_command(label="Opslaan", command=self.save_project)
        project_menu.add_command(label="Opslaan als...", command=self.save_project_as)
        project_menu.add_separator()
        project_menu.add_command(label="Exporteer SVG...", command=self.export_svg)
        project_menu.add_command(label="Exporteer PNG...", command=self.export_png)
        project_menu.add_command(label="Exporteer PDF...", command=self.export_pdf)
        project_menu.add_command(label="Exporteer netlist CSV...", command=self.export_netlist_csv)
        project_menu.add_command(label="Exporteer BOM CSV...", command=self.export_bom_csv)
        menu.add_cascade(label="Bestand", menu=project_menu)

        edit_menu = tk.Menu(menu, tearoff=0)
        edit_menu.add_command(label="Undo", command=self.undo, state=("normal" if self._history_undo else "disabled"))
        edit_menu.add_command(label="Redo", command=self.redo, state=("normal" if self._history_redo else "disabled"))
        edit_menu.add_separator()
        edit_menu.add_command(label="Plak uit klembord", command=self.paste_from_clipboard)
        edit_menu.add_command(label="Afbeelding importeren...", command=self.import_image_note)
        edit_menu.add_separator()
        edit_menu.add_command(label="Dupliceer selectie", command=self.duplicate_selected, state=("normal" if self.selected else "disabled"))
        edit_menu.add_command(label="Verwijder selectie", command=self.delete_selected, state=("normal" if self.selected else "disabled"))
        menu.add_cascade(label="Bewerken", menu=edit_menu)

        view_menu = tk.Menu(menu, tearoff=0)
        view_menu.add_command(label="Fit blad in beeld", command=self.fit_page_to_view)
        view_menu.add_checkbutton(label="Grid snap", variable=self.snap_grid_enabled_var, command=self._on_snap_settings_changed)
        view_menu.add_checkbutton(label="Endpoint snap", variable=self.snap_endpoint_enabled_var, command=self._on_snap_settings_changed)
        view_menu.add_separator()
        self._add_ui_scale_menu(view_menu)
        self._add_panel_menu(view_menu)
        menu.add_cascade(label="Beeld", menu=view_menu)

        tools_menu = tk.Menu(menu, tearoff=0)
        tools_menu.add_command(label="Projectcontrole (DRC)", command=self.run_project_check)
        tools_menu.add_command(label="Projectinventaris", command=self.show_project_inventory)
        tools_menu.add_separator()
        tools_menu.add_command(label="Selectie opslaan als blok...", command=self.save_selection_as_block)
        tools_menu.add_command(label="Blok invoegen", command=self.insert_selected_block)
        tools_menu.add_separator()
        tools_menu.add_command(label="Eigenschappenpaneel focussen", command=self.focus_properties_panel)
        menu.add_cascade(label="Tools", menu=tools_menu)

    def _show_panel_context_menu(self, event):
        menu = tk.Menu(self, tearoff=0)
        self._populate_panel_menu(menu)
        menu.add_separator()
        self._add_ui_scale_menu(menu)
        self._popup_menu(event, menu)
        return "break"

    def _build_menubar(self):
        menubar = tk.Menu(self)

        file_menu = tk.Menu(menubar, tearoff=0)
        file_menu.add_command(label="Nieuw project", command=self.new_project, accelerator="Ctrl+N")
        file_menu.add_command(label="Nieuw uit sjabloon...", command=self.new_project_from_template)
        file_menu.add_command(label="Open project...", command=self.open_project, accelerator="Ctrl+O")
        file_menu.add_separator()
        file_menu.add_command(label="Opslaan", command=self.save_project, accelerator="Ctrl+S")
        file_menu.add_command(label="Opslaan als...", command=self.save_project_as, accelerator="Ctrl+Shift+S")
        file_menu.add_separator()
        file_menu.add_command(label="Exporteer SVG...", command=self.export_svg)
        file_menu.add_command(label="Exporteer PNG...", command=self.export_png)
        file_menu.add_command(label="Exporteer PDF...", command=self.export_pdf)
        file_menu.add_command(label="Exporteer netlist CSV...", command=self.export_netlist_csv)
        file_menu.add_command(label="Exporteer BOM CSV...", command=self.export_bom_csv)
        file_menu.add_separator()
        file_menu.add_command(label="Afsluiten", command=self._on_close)
        menubar.add_cascade(label="Bestand", menu=file_menu)

        edit_menu = tk.Menu(menubar, tearoff=0)
        edit_menu.add_command(label="Undo", command=self.undo, accelerator="Ctrl+Z")
        edit_menu.add_command(label="Redo", command=self.redo, accelerator="Ctrl+Y")
        edit_menu.add_separator()
        edit_menu.add_command(label="Plak uit klembord", command=self.paste_from_clipboard, accelerator="Ctrl+V")
        edit_menu.add_command(label="Afbeelding importeren...", command=self.import_image_note)
        edit_menu.add_separator()
        edit_menu.add_command(label="Dupliceer selectie", command=self.duplicate_selected, accelerator="Ctrl+D")
        edit_menu.add_command(label="Verwijder selectie", command=self.delete_selected, accelerator="Delete")
        menubar.add_cascade(label="Bewerken", menu=edit_menu)

        view_menu = tk.Menu(menubar, tearoff=0)
        view_menu.add_command(label="Fit blad in beeld", command=self.fit_page_to_view)
        view_menu.add_checkbutton(label="Grid snap", variable=self.snap_grid_enabled_var, command=self._on_snap_settings_changed)
        view_menu.add_checkbutton(label="Endpoint snap", variable=self.snap_endpoint_enabled_var, command=self._on_snap_settings_changed)
        view_menu.add_separator()
        view_menu.add_checkbutton(label="Prestatiemeter (redraw-tijd)", variable=self.perf_meter_var, command=self._toggle_perf_meter)
        view_menu.add_separator()
        self._add_theme_menu(view_menu)
        self._add_ui_scale_menu(view_menu)
        self._add_panel_menu(view_menu)
        menubar.add_cascade(label="Beeld", menu=view_menu)

        tools_menu = tk.Menu(menubar, tearoff=0)
        tools_menu.add_command(label="Projectcontrole (DRC)", command=self.run_project_check)
        tools_menu.add_command(label="Netlist uit tekening afleiden", command=self.derive_netlist_from_geometry)
        tools_menu.add_command(label="Projectinventaris", command=self.show_project_inventory)
        tools_menu.add_command(label="Stress-test: vul met draden…", command=self.stress_fill_wires)
        tools_menu.add_separator()
        tools_menu.add_command(label="Selectie opslaan als blok...", command=self.save_selection_as_block)
        tools_menu.add_command(label="Blok invoegen", command=self.insert_selected_block)
        tools_menu.add_separator()
        tools_menu.add_command(label="Eigenschappenpaneel focussen", command=self.focus_properties_panel)
        menubar.add_cascade(label="Tools", menu=tools_menu)

        help_menu = tk.Menu(menubar, tearoff=0)
        help_menu.add_command(label="Sneltoetsen & muisbediening", command=self.show_shortcuts_dialog, accelerator="F1")
        help_menu.add_separator()
        help_menu.add_command(label="Zoek naar updates...", command=self.check_for_updates_interactive)
        menubar.add_cascade(label="Help", menu=help_menu)

        self.config(menu=menubar)

    # ---------------- Auto-update ----------------
