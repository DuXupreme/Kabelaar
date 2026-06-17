#!/usr/bin/env python3
"""Kabelboom Studio."""

from __future__ import annotations

import csv
import json
import math
import re
import tkinter as tk
from dataclasses import asdict, dataclass
from pathlib import Path
from tkinter import filedialog, messagebox, simpledialog, ttk
from typing import Dict, List, Tuple
from xml.sax.saxutils import escape

from app_settings import existing_dir, load_app_settings, parent_dir, update_app_settings
from project_io import write_text_atomic
from ui_scaling import UI_SCALE_LABELS, enable_dpi_awareness, normalize_ui_scale_percent, schedule_window_scaling, set_ui_scale

APP_TITLE = "Kabelboom Studio"
PROJECT_SCHEMA_VERSION = 1
APP_SETTINGS_KEY = "kabelboom_studio"


@dataclass
class Connector:
    ref: str
    name: str
    part_number: str
    pin_count: int
    side: str  # Left / Right


@dataclass
class Wire:
    wire_id: str
    signal_name: str
    from_connector: str
    from_pin: str
    to_connector: str
    to_pin: str
    color: str
    cross_section_mm2: float
    length_mm: float
    shielded: bool


def to_int(value, fallback=1) -> int:
    try:
        n = int(str(value).strip())
        if n > 0:
            return n
    except (TypeError, ValueError):
        pass
    return fallback


def to_float(value, fallback=0.0) -> float:
    try:
        f = float(str(value).replace(",", ".").strip())
        if f >= 0:
            return f
    except (TypeError, ValueError):
        pass
    return fallback


def safe_filename(name: str) -> str:
    cleaned = re.sub(r"[^A-Za-z0-9._-]+", "_", (name or "").strip())
    cleaned = cleaned.strip("._")
    return cleaned or "kabelboom"


def color_hex(name: str) -> str:
    c = (name or "").strip().lower()
    mapping = {
        "zwart": "#111111",
        "black": "#111111",
        "rood": "#c92a2a",
        "red": "#c92a2a",
        "blauw": "#1d4ed8",
        "blue": "#1d4ed8",
        "groen": "#2f9e44",
        "green": "#2f9e44",
        "geel": "#e7b416",
        "yellow": "#e7b416",
        "wit": "#f8f9fa",
        "white": "#f8f9fa",
        "oranje": "#e67700",
        "orange": "#e67700",
        "bruin": "#8d5b2f",
        "brown": "#8d5b2f",
        "grijs": "#6c757d",
        "gray": "#6c757d",
    }
    if c in mapping:
        return mapping[c]
    if re.fullmatch(r"#?[0-9a-fA-F]{6}", c):
        return c if c.startswith("#") else f"#{c}"
    return "#444444"


class FormDialog(simpledialog.Dialog):
    """Kleine dynamische form dialoog."""

    def __init__(self, parent, title: str, fields: List[dict], initial: dict | None = None):
        self.fields = fields
        self.initial = initial or {}
        self.vars: Dict[str, tk.Variable] = {}
        self.result_data: dict | None = None
        super().__init__(parent, title=title)

    def body(self, master):
        master.columnconfigure(1, weight=1)
        for row, field in enumerate(self.fields):
            key = field["key"]
            kind = field.get("type", "text")
            label = field.get("label", key)
            default = self.initial.get(key, field.get("default", ""))
            ttk.Label(master, text=label).grid(row=row, column=0, sticky="w", padx=4, pady=4)

            if kind == "bool":
                var = tk.BooleanVar(value=bool(default))
                ttk.Checkbutton(master, variable=var).grid(row=row, column=1, sticky="w", padx=4, pady=4)
            elif kind == "choice":
                var = tk.StringVar(value=str(default))
                ttk.Combobox(
                    master,
                    textvariable=var,
                    values=field.get("choices", []),
                    state="readonly",
                ).grid(row=row, column=1, sticky="ew", padx=4, pady=4)
            else:
                var = tk.StringVar(value=str(default))
                ttk.Entry(master, textvariable=var).grid(row=row, column=1, sticky="ew", padx=4, pady=4)
            self.vars[key] = var
        return master

    def validate(self):
        data = {}
        for field in self.fields:
            key = field["key"]
            kind = field.get("type", "text")
            raw = self.vars[key].get() if kind != "bool" else bool(self.vars[key].get())

            if field.get("required") and (raw is None or str(raw).strip() == ""):
                messagebox.showerror(APP_TITLE, f"Veld '{field.get('label', key)}' is verplicht.")
                return False
            if kind == "int":
                val = to_int(raw, fallback=-1)
                if val <= 0:
                    messagebox.showerror(APP_TITLE, f"Veld '{field.get('label', key)}' moet > 0 zijn.")
                    return False
                data[key] = val
            elif kind == "float":
                val = to_float(raw, fallback=-1)
                if val < 0:
                    messagebox.showerror(APP_TITLE, f"Veld '{field.get('label', key)}' moet >= 0 zijn.")
                    return False
                data[key] = val
            elif kind == "bool":
                data[key] = bool(raw)
            else:
                data[key] = str(raw).strip()

        for field in self.fields:
            key = field["key"]
            if field.get("type") == "choice":
                choices = field.get("choices", [])
                if data[key] not in choices:
                    messagebox.showerror(APP_TITLE, f"Veld '{field.get('label', key)}' heeft een ongeldige keuze.")
                    return False

        self.result_data = data
        return True


class KabelboomStudio(tk.Tk):
    def __init__(self):
        super().__init__()
        self.title(APP_TITLE)
        self.geometry("1320x840")
        self.settings = load_app_settings(APP_SETTINGS_KEY)
        self._ui_scale_percent = normalize_ui_scale_percent(self.settings.get("ui_scale_percent", 100))
        schedule_window_scaling(self, design_size=(1320, 840), min_size=(1080, 700))

        self.project_path: Path | None = None
        self.connectors: List[Connector] = []
        self.wires: List[Wire] = []
        self._saved_snapshot = ""

        self.name_var = tk.StringVar(value="Nieuwe kabelboom")
        self.part_var = tk.StringVar(value="")
        self.rev_var = tk.StringVar(value="A")
        self.note_var = tk.StringVar(value="")
        self.ui_scale_var = tk.StringVar(value=f"{self._ui_scale_percent}%")

        self._build_ui()
        self._refresh_tables()
        self._draw_preview()
        self._mark_saved()
        self.protocol("WM_DELETE_WINDOW", self._on_close)

    def _build_ui(self):
        self.columnconfigure(0, weight=1)
        self.rowconfigure(2, weight=1)

        bar = ttk.Frame(self, padding=8)
        bar.grid(row=0, column=0, sticky="ew")
        bar.columnconfigure(10, weight=1)

        ttk.Button(bar, text="Nieuw", command=self.new_project).grid(row=0, column=0, padx=3)
        ttk.Button(bar, text="Open JSON", command=self.load_project).grid(row=0, column=1, padx=3)
        ttk.Button(bar, text="Opslaan JSON", command=self.save_project).grid(row=0, column=2, padx=3)
        ttk.Button(bar, text="Opslaan Als", command=self.save_project_as).grid(row=0, column=3, padx=3)
        ttk.Separator(bar, orient="vertical").grid(row=0, column=4, sticky="ns", padx=6)
        ttk.Button(bar, text="Export CSV", command=self.export_csv).grid(row=0, column=5, padx=3)
        ttk.Button(bar, text="Export SVG", command=self.export_svg).grid(row=0, column=6, padx=3)
        ttk.Button(bar, text="Export Alles", command=self.export_all).grid(row=0, column=7, padx=3)
        ttk.Label(bar, text="UI").grid(row=0, column=8, padx=(10, 3))
        self.ui_scale_combo = ttk.Combobox(bar, textvariable=self.ui_scale_var, values=UI_SCALE_LABELS, state="readonly", width=6)
        self.ui_scale_combo.grid(row=0, column=9, padx=3)
        self.ui_scale_combo.bind("<<ComboboxSelected>>", self._on_ui_scale_changed)
        self.status = ttk.Label(bar, text="Klaar")
        self.status.grid(row=0, column=10, sticky="e")

        meta = ttk.LabelFrame(self, text="Project", padding=8)
        meta.grid(row=1, column=0, sticky="ew", padx=10, pady=(0, 8))
        for i in range(8):
            meta.columnconfigure(i, weight=1 if i in {1, 3, 5, 7} else 0)

        ttk.Label(meta, text="Naam").grid(row=0, column=0, sticky="w")
        ttk.Entry(meta, textvariable=self.name_var).grid(row=0, column=1, sticky="ew", padx=4)
        ttk.Label(meta, text="Part number").grid(row=0, column=2, sticky="w")
        ttk.Entry(meta, textvariable=self.part_var).grid(row=0, column=3, sticky="ew", padx=4)
        ttk.Label(meta, text="Rev").grid(row=0, column=4, sticky="w")
        ttk.Entry(meta, textvariable=self.rev_var).grid(row=0, column=5, sticky="ew", padx=4)
        ttk.Label(meta, text="Notitie").grid(row=0, column=6, sticky="w")
        ttk.Entry(meta, textvariable=self.note_var).grid(row=0, column=7, sticky="ew", padx=4)

        pane = ttk.PanedWindow(self, orient="horizontal")
        pane.grid(row=2, column=0, sticky="nsew", padx=10, pady=(0, 10))

        self._build_connectors_panel(pane)
        self._build_wires_panel(pane)
        self._build_preview_panel(pane)

        for var in [self.name_var, self.part_var, self.rev_var, self.note_var]:
            var.trace_add("write", lambda *_: self._set_status("Niet opgeslagen"))

    def _build_connectors_panel(self, pane):
        frame = ttk.Frame(pane, padding=8)
        frame.columnconfigure(0, weight=1)
        frame.rowconfigure(1, weight=1)
        ttk.Label(frame, text="Connectoren").grid(row=0, column=0, sticky="w")

        self.connectors_tree = ttk.Treeview(
            frame,
            columns=("ref", "name", "part", "pins", "side"),
            show="headings",
            height=14,
        )
        cols = [("ref", "Ref", 80), ("name", "Naam", 140), ("part", "Part", 130), ("pins", "Pins", 60), ("side", "Side", 65)]
        for key, label, width in cols:
            self.connectors_tree.heading(key, text=label)
            self.connectors_tree.column(key, width=width, anchor="w")
        self.connectors_tree.grid(row=1, column=0, sticky="nsew", pady=(4, 6))

        buttons = ttk.Frame(frame)
        buttons.grid(row=2, column=0, sticky="ew")
        ttk.Button(buttons, text="Toevoegen", command=self.add_connector).pack(side="left", padx=(0, 4))
        ttk.Button(buttons, text="Bewerken", command=self.edit_connector).pack(side="left", padx=4)
        ttk.Button(buttons, text="Verwijderen", command=self.delete_connector).pack(side="left", padx=4)
        pane.add(frame, weight=3)

    def _build_wires_panel(self, pane):
        frame = ttk.Frame(pane, padding=8)
        frame.columnconfigure(0, weight=1)
        frame.rowconfigure(1, weight=1)
        ttk.Label(frame, text="Draden").grid(row=0, column=0, sticky="w")

        self.wires_tree = ttk.Treeview(
            frame,
            columns=("id", "signal", "from", "to", "color", "mm2", "len", "shield"),
            show="headings",
            height=14,
        )
        cols = [
            ("id", "ID", 70),
            ("signal", "Signaal", 120),
            ("from", "Van", 100),
            ("to", "Naar", 100),
            ("color", "Kleur", 80),
            ("mm2", "mm2", 60),
            ("len", "Len", 60),
            ("shield", "Shield", 60),
        ]
        for key, label, width in cols:
            self.wires_tree.heading(key, text=label)
            self.wires_tree.column(key, width=width, anchor="w")
        self.wires_tree.grid(row=1, column=0, sticky="nsew", pady=(4, 6))

        buttons = ttk.Frame(frame)
        buttons.grid(row=2, column=0, sticky="ew")
        ttk.Button(buttons, text="Toevoegen", command=self.add_wire).pack(side="left", padx=(0, 4))
        ttk.Button(buttons, text="Bewerken", command=self.edit_wire).pack(side="left", padx=4)
        ttk.Button(buttons, text="Verwijderen", command=self.delete_wire).pack(side="left", padx=4)
        pane.add(frame, weight=4)

    def _build_preview_panel(self, pane):
        frame = ttk.Frame(pane, padding=8)
        frame.columnconfigure(0, weight=1)
        frame.rowconfigure(1, weight=1)
        ttk.Label(frame, text="Preview").grid(row=0, column=0, sticky="w")
        self.canvas = tk.Canvas(frame, background="#f8fafc", highlightthickness=1, highlightbackground="#d2dae2")
        self.canvas.grid(row=1, column=0, sticky="nsew", pady=(4, 0))
        self.canvas.bind("<Configure>", lambda _e: self._draw_preview())
        pane.add(frame, weight=6)

    # CRUD: connectors
    def add_connector(self):
        fields = [
            {"key": "ref", "label": "Ref (bijv J100)", "required": True},
            {"key": "name", "label": "Naam", "required": True},
            {"key": "part_number", "label": "Part number"},
            {"key": "pin_count", "label": "Aantal pins", "type": "int", "default": 12},
            {"key": "side", "label": "Side", "type": "choice", "choices": ["Left", "Right"], "default": "Left"},
        ]
        dlg = FormDialog(self, "Connector toevoegen", fields)
        if not dlg.result_data:
            return
        data = dlg.result_data
        ref = data["ref"].upper()
        if any(c.ref == ref for c in self.connectors):
            messagebox.showerror(APP_TITLE, f"Connector '{ref}' bestaat al.")
            return
        self.connectors.append(
            Connector(ref=ref, name=data["name"], part_number=data["part_number"], pin_count=data["pin_count"], side=data["side"])
        )
        self.connectors.sort(key=lambda c: c.ref)
        self._after_edit()

    def edit_connector(self):
        idx = self._selected_index(self.connectors_tree, len(self.connectors), "Selecteer een connector.")
        if idx is None:
            return
        c = self.connectors[idx]
        fields = [
            {"key": "ref", "label": "Ref (bijv J100)", "required": True},
            {"key": "name", "label": "Naam", "required": True},
            {"key": "part_number", "label": "Part number"},
            {"key": "pin_count", "label": "Aantal pins", "type": "int"},
            {"key": "side", "label": "Side", "type": "choice", "choices": ["Left", "Right"]},
        ]
        dlg = FormDialog(self, "Connector bewerken", fields, initial=asdict(c))
        if not dlg.result_data:
            return
        d = dlg.result_data
        new_ref = d["ref"].upper()
        if new_ref != c.ref and any(x.ref == new_ref for x in self.connectors):
            messagebox.showerror(APP_TITLE, f"Connector '{new_ref}' bestaat al.")
            return
        old_ref = c.ref
        self.connectors[idx] = Connector(new_ref, d["name"], d["part_number"], d["pin_count"], d["side"])
        for w in self.wires:
            if w.from_connector == old_ref:
                w.from_connector = new_ref
            if w.to_connector == old_ref:
                w.to_connector = new_ref
        self.connectors.sort(key=lambda x: x.ref)
        self._after_edit()

    def delete_connector(self):
        idx = self._selected_index(self.connectors_tree, len(self.connectors), "Selecteer een connector.")
        if idx is None:
            return
        ref = self.connectors[idx].ref
        related = [w for w in self.wires if w.from_connector == ref or w.to_connector == ref]
        if related and not messagebox.askyesno(APP_TITLE, f"{ref} wordt gebruikt in {len(related)} draad(en). Ook verwijderen?"):
            return
        del self.connectors[idx]
        self.wires = [w for w in self.wires if w not in related]
        self._after_edit()

    # CRUD: wires
    def add_wire(self):
        if len(self.connectors) < 2:
            messagebox.showinfo(APP_TITLE, "Voeg eerst minimaal 2 connectoren toe.")
            return
        refs = [c.ref for c in self.connectors]
        fields = [
            {"key": "wire_id", "label": "Wire ID", "required": True},
            {"key": "signal_name", "label": "Signaal"},
            {"key": "from_connector", "label": "Van connector", "type": "choice", "choices": refs, "default": refs[0]},
            {"key": "from_pin", "label": "Van pin", "required": True, "default": "1"},
            {"key": "to_connector", "label": "Naar connector", "type": "choice", "choices": refs, "default": refs[-1]},
            {"key": "to_pin", "label": "Naar pin", "required": True, "default": "1"},
            {"key": "color", "label": "Kleur", "default": "zwart"},
            {"key": "cross_section_mm2", "label": "Doorsnede mm2", "type": "float", "default": 0.35},
            {"key": "length_mm", "label": "Lengte mm", "type": "float", "default": 1000},
            {"key": "shielded", "label": "Shielded", "type": "bool", "default": False},
        ]
        dlg = FormDialog(self, "Draad toevoegen", fields)
        if not dlg.result_data:
            return
        d = dlg.result_data
        if any(w.wire_id == d["wire_id"] for w in self.wires):
            messagebox.showerror(APP_TITLE, f"Wire ID '{d['wire_id']}' bestaat al.")
            return
        self.wires.append(Wire(**d))
        self.wires.sort(key=lambda w: w.wire_id)
        self._after_edit()

    def edit_wire(self):
        idx = self._selected_index(self.wires_tree, len(self.wires), "Selecteer een draad.")
        if idx is None:
            return
        w = self.wires[idx]
        refs = [c.ref for c in self.connectors]
        fields = [
            {"key": "wire_id", "label": "Wire ID", "required": True},
            {"key": "signal_name", "label": "Signaal"},
            {"key": "from_connector", "label": "Van connector", "type": "choice", "choices": refs},
            {"key": "from_pin", "label": "Van pin", "required": True},
            {"key": "to_connector", "label": "Naar connector", "type": "choice", "choices": refs},
            {"key": "to_pin", "label": "Naar pin", "required": True},
            {"key": "color", "label": "Kleur"},
            {"key": "cross_section_mm2", "label": "Doorsnede mm2", "type": "float"},
            {"key": "length_mm", "label": "Lengte mm", "type": "float"},
            {"key": "shielded", "label": "Shielded", "type": "bool"},
        ]
        dlg = FormDialog(self, "Draad bewerken", fields, initial=asdict(w))
        if not dlg.result_data:
            return
        d = dlg.result_data
        if d["wire_id"] != w.wire_id and any(x.wire_id == d["wire_id"] for x in self.wires):
            messagebox.showerror(APP_TITLE, f"Wire ID '{d['wire_id']}' bestaat al.")
            return
        self.wires[idx] = Wire(**d)
        self.wires.sort(key=lambda x: x.wire_id)
        self._after_edit()

    def delete_wire(self):
        idx = self._selected_index(self.wires_tree, len(self.wires), "Selecteer een draad.")
        if idx is None:
            return
        del self.wires[idx]
        self._after_edit()

    def _selected_index(self, tree: ttk.Treeview, size: int, empty_msg: str):
        sel = tree.selection()
        if not sel:
            messagebox.showinfo(APP_TITLE, empty_msg)
            return None
        idx = int(sel[0])
        if idx < 0 or idx >= size:
            return None
        return idx

    def _after_edit(self):
        self._refresh_tables()
        self._draw_preview()
        self._set_status("Niet opgeslagen")

    def _refresh_tables(self):
        self.connectors_tree.delete(*self.connectors_tree.get_children())
        for i, c in enumerate(self.connectors):
            self.connectors_tree.insert("", "end", iid=str(i), values=(c.ref, c.name, c.part_number, c.pin_count, c.side))

        self.wires_tree.delete(*self.wires_tree.get_children())
        for i, w in enumerate(self.wires):
            self.wires_tree.insert(
                "",
                "end",
                iid=str(i),
                values=(
                    w.wire_id,
                    w.signal_name,
                    f"{w.from_connector}:{w.from_pin}",
                    f"{w.to_connector}:{w.to_pin}",
                    w.color,
                    f"{w.cross_section_mm2:g}",
                    f"{w.length_mm:g}",
                    "Ja" if w.shielded else "Nee",
                ),
            )

    # Drawing
    def _connector_positions(self, width: float, height: float):
        left = sorted([c for c in self.connectors if c.side == "Left"], key=lambda x: x.ref)
        right = sorted([c for c in self.connectors if c.side != "Left"], key=lambda x: x.ref)
        result: Dict[str, Tuple[float, float, float, float]] = {}
        box_w = 150

        def place(items: List[Connector], x: float):
            if not items:
                return
            hs = [max(70, c.pin_count * 15 + 26) for c in items]
            total = sum(hs) + 20 * (len(hs) - 1)
            y = max(25, (height - total) / 2)
            for c, h in zip(items, hs):
                result[c.ref] = (x, y, box_w, h)
                y += h + 20

        place(left, 45)
        place(right, max(240, width - 195))
        return result

    def _alias_pin_map(self):
        alias: Dict[str, Dict[str, int]] = {}
        pin_count = {c.ref: c.pin_count for c in self.connectors}
        for w in self.wires:
            for ref, pin in [(w.from_connector, w.from_pin), (w.to_connector, w.to_pin)]:
                alias.setdefault(ref, {})
                pin_text = str(pin).strip()
                if not pin_text or re.fullmatch(r"\d+", pin_text):
                    continue
                if pin_text not in alias[ref]:
                    alias[ref][pin_text] = min(len(alias[ref]) + 1, pin_count.get(ref, 1))
        return alias

    def _pin_index(self, ref: str, pin: str, alias: Dict[str, Dict[str, int]]) -> int:
        count = next((c.pin_count for c in self.connectors if c.ref == ref), 1)
        p = str(pin).strip()
        if re.fullmatch(r"\d+", p):
            return min(max(1, to_int(p, 1)), count)
        if p and p in alias.get(ref, {}):
            return min(max(1, alias[ref][p]), count)
        return 1

    def _pin_xy(self, c: Connector, box: Tuple[float, float, float, float], pin_index: int):
        x, y, bw, bh = box
        step = (bh - 26) / max(1, c.pin_count)
        py = y + 16 + (pin_index - 0.5) * step
        if c.side == "Left":
            return x + bw, py
        return x, py

    def _draw_preview(self):
        cv = self.canvas
        cv.delete("all")
        w = max(800, cv.winfo_width())
        h = max(500, cv.winfo_height())
        cv.create_rectangle(0, 0, w, h, fill="#f8fafc", outline="")

        if not self.connectors:
            cv.create_text(w / 2, h / 2, text="Voeg connectoren en draden toe.", fill="#64748b", font=("Segoe UI", 11))
            return

        pos = self._connector_positions(w, h)
        alias = self._alias_pin_map()
        by_ref = {c.ref: c for c in self.connectors}

        for c in self.connectors:
            box = pos.get(c.ref)
            if not box:
                continue
            x, y, bw, bh = box
            cv.create_rectangle(x, y, x + bw, y + bh, fill="#ffffff", outline="#334155", width=2)
            cv.create_text(x + bw / 2, y + 14, text=f"{c.ref} - {c.name}", fill="#0f172a", font=("Segoe UI", 9, "bold"))
            cv.create_text(x + bw / 2, y + bh - 11, text=f"{c.pin_count} pin | {c.part_number or '-'}", fill="#475569", font=("Segoe UI", 8))
            step = (bh - 26) / max(1, c.pin_count)
            for pin in range(1, c.pin_count + 1):
                py = y + 16 + (pin - 0.5) * step
                if c.side == "Left":
                    cv.create_line(x + bw - 10, py, x + bw, py, fill="#475569")
                else:
                    cv.create_line(x, py, x + 10, py, fill="#475569")

        for wire in self.wires:
            c1 = by_ref.get(wire.from_connector)
            c2 = by_ref.get(wire.to_connector)
            b1 = pos.get(wire.from_connector)
            b2 = pos.get(wire.to_connector)
            if not c1 or not c2 or not b1 or not b2:
                continue
            p1 = self._pin_index(c1.ref, wire.from_pin, alias)
            p2 = self._pin_index(c2.ref, wire.to_pin, alias)
            x1, y1 = self._pin_xy(c1, b1, p1)
            x2, y2 = self._pin_xy(c2, b2, p2)
            mx = (x1 + x2) / 2
            sw = 1.2 + math.sqrt(max(0.05, wire.cross_section_mm2)) * 1.2
            cv.create_line(
                x1,
                y1,
                x1 + (mx - x1) * 0.6,
                y1,
                x2 + (mx - x2) * 0.6,
                y2,
                x2,
                y2,
                smooth=True,
                splinesteps=20,
                fill=color_hex(wire.color),
                width=sw,
            )
            cv.create_text(mx, (y1 + y2) / 2 - 7, text=wire.signal_name or wire.wire_id, fill="#1e293b", font=("Segoe UI", 7))

        cv.create_text(12, 12, anchor="nw", text=f"{self.name_var.get()} | Rev {self.rev_var.get() or '-'}", fill="#0f172a", font=("Segoe UI", 10, "bold"))

    # Project IO
    def _set_status(self, text: str):
        self.status.configure(text=text)

    def _save_settings(self, **values):
        self.settings = update_app_settings(APP_SETTINGS_KEY, **values)

    def _initial_dir(self, key: str) -> str | None:
        return existing_dir(self.settings.get(key))

    def _remember_dialog_path(self, key: str, path_text: str):
        self._save_settings(**{key: parent_dir(path_text)})

    def _on_ui_scale_changed(self, _event=None):
        percent = set_ui_scale(self, self.ui_scale_var.get())
        self.ui_scale_var.set(f"{percent}%")
        self._save_settings(ui_scale_percent=percent)
        self._draw_preview()
        self._set_status(f"UI schaal: {percent}%")

    def _project_data(self):
        return {
            "schema_version": PROJECT_SCHEMA_VERSION,
            "name": self.name_var.get().strip(),
            "part_number": self.part_var.get().strip(),
            "revision": self.rev_var.get().strip(),
            "notes": self.note_var.get().strip(),
            "connectors": [asdict(c) for c in self.connectors],
            "wires": [asdict(w) for w in self.wires],
        }

    def _snapshot_project(self) -> str:
        return json.dumps(self._project_data(), ensure_ascii=False, sort_keys=True)

    def _mark_saved(self):
        self._saved_snapshot = self._snapshot_project()

    def _has_unsaved_changes(self) -> bool:
        return self._snapshot_project() != self._saved_snapshot

    def _confirm_discard_changes(self) -> bool:
        if not self._has_unsaved_changes():
            return True
        answer = messagebox.askyesnocancel(
            APP_TITLE,
            "Er zijn niet-opgeslagen wijzigingen.\n\nWil je het project eerst opslaan?",
        )
        if answer is None:
            return False
        if answer:
            return bool(self.save_project())
        return True

    def _on_close(self):
        if self._confirm_discard_changes():
            self.destroy()

    def _load_data(self, data: dict):
        self.name_var.set(str(data.get("name", "")).strip() or "Nieuwe kabelboom")
        self.part_var.set(str(data.get("part_number", "")).strip())
        self.rev_var.set(str(data.get("revision", "")).strip() or "A")
        self.note_var.set(str(data.get("notes", "")).strip())
        self.connectors = [
            Connector(
                ref=str(x.get("ref", "")).strip().upper(),
                name=str(x.get("name", "")).strip(),
                part_number=str(x.get("part_number", "")).strip(),
                pin_count=to_int(x.get("pin_count", 1), 1),
                side=(str(x.get("side", "Left")).strip() or "Left"),
            )
            for x in data.get("connectors", [])
        ]
        self.wires = [
            Wire(
                wire_id=str(x.get("wire_id", "")).strip(),
                signal_name=str(x.get("signal_name", "")).strip(),
                from_connector=str(x.get("from_connector", "")).strip().upper(),
                from_pin=str(x.get("from_pin", "")).strip(),
                to_connector=str(x.get("to_connector", "")).strip().upper(),
                to_pin=str(x.get("to_pin", "")).strip(),
                color=str(x.get("color", "")).strip(),
                cross_section_mm2=to_float(x.get("cross_section_mm2", 0.0), 0.0),
                length_mm=to_float(x.get("length_mm", 0.0), 0.0),
                shielded=bool(x.get("shielded", False)),
            )
            for x in data.get("wires", [])
        ]
        self._refresh_tables()
        self._draw_preview()

    def new_project(self):
        if not self._confirm_discard_changes():
            return
        self.project_path = None
        self._load_data({"name": "Nieuwe kabelboom", "revision": "A", "connectors": [], "wires": []})
        self._mark_saved()
        self._set_status("Nieuw project")

    def load_project(self):
        if not self._confirm_discard_changes():
            return
        kwargs = {
            "title": "Open JSON project",
            "filetypes": [("JSON", "*.json"), ("Alle files", "*.*")],
        }
        initialdir = self._initial_dir("last_project_dir")
        if initialdir:
            kwargs["initialdir"] = initialdir
        path = filedialog.askopenfilename(**kwargs)
        if not path:
            return
        try:
            data = json.loads(Path(path).read_text(encoding="utf-8"))
            self._load_data(data)
            self.project_path = Path(path)
            self._remember_dialog_path("last_project_dir", path)
            self._mark_saved()
            self._set_status(f"Geopend: {self.project_path.name}")
        except Exception as exc:
            messagebox.showerror(APP_TITLE, f"Openen mislukt:\n{exc}")

    def save_project(self):
        if self.project_path is None:
            return self.save_project_as()
        return self._write_project(self.project_path)

    def save_project_as(self):
        kwargs = {
            "title": "Opslaan als",
            "defaultextension": ".json",
            "initialfile": f"{safe_filename(self.name_var.get())}.json",
            "filetypes": [("JSON", "*.json"), ("Alle files", "*.*")],
        }
        initialdir = self._initial_dir("last_project_dir")
        if initialdir:
            kwargs["initialdir"] = initialdir
        path = filedialog.asksaveasfilename(**kwargs)
        if not path:
            return False
        self.project_path = Path(path)
        self._remember_dialog_path("last_project_dir", path)
        return self._write_project(self.project_path)

    def _write_project(self, path: Path):
        try:
            write_text_atomic(path, json.dumps(self._project_data(), indent=2, ensure_ascii=False))
            self._mark_saved()
            self._set_status(f"Opgeslagen: {path.name}")
            return True
        except Exception as exc:
            messagebox.showerror(APP_TITLE, f"Opslaan mislukt:\n{exc}")
            return False

    # Export
    def export_csv(self):
        kwargs = {"title": "Kies map voor CSV export"}
        initialdir = self._initial_dir("last_export_dir")
        if initialdir:
            kwargs["initialdir"] = initialdir
        folder = filedialog.askdirectory(**kwargs)
        if not folder:
            return
        self._save_settings(last_export_dir=str(Path(folder)))
        base = safe_filename(self.name_var.get())
        bom = Path(folder) / f"{base}_bom.csv"
        net = Path(folder) / f"{base}_netlist.csv"
        try:
            self._write_bom_csv(bom)
            self._write_net_csv(net)
            self._set_status("CSV export klaar")
            messagebox.showinfo(APP_TITLE, f"Gemaakt:\n{bom}\n{net}")
        except Exception as exc:
            messagebox.showerror(APP_TITLE, f"CSV export mislukt:\n{exc}")

    def export_svg(self):
        kwargs = {
            "title": "SVG export",
            "defaultextension": ".svg",
            "initialfile": f"{safe_filename(self.name_var.get())}_overview.svg",
            "filetypes": [("SVG", "*.svg"), ("Alle files", "*.*")],
        }
        initialdir = self._initial_dir("last_export_dir")
        if initialdir:
            kwargs["initialdir"] = initialdir
        path = filedialog.asksaveasfilename(**kwargs)
        if not path:
            return
        try:
            self._remember_dialog_path("last_export_dir", path)
            self._write_svg(Path(path))
            self._set_status("SVG export klaar")
            messagebox.showinfo(APP_TITLE, f"Gemaakt:\n{path}")
        except Exception as exc:
            messagebox.showerror(APP_TITLE, f"SVG export mislukt:\n{exc}")

    def export_all(self):
        kwargs = {"title": "Kies map voor export"}
        initialdir = self._initial_dir("last_export_dir")
        if initialdir:
            kwargs["initialdir"] = initialdir
        folder = filedialog.askdirectory(**kwargs)
        if not folder:
            return
        self._save_settings(last_export_dir=str(Path(folder)))
        base = safe_filename(self.name_var.get())
        bom = Path(folder) / f"{base}_bom.csv"
        net = Path(folder) / f"{base}_netlist.csv"
        svg = Path(folder) / f"{base}_overview.svg"
        try:
            self._write_bom_csv(bom)
            self._write_net_csv(net)
            self._write_svg(svg)
            self._set_status("Export klaar")
            messagebox.showinfo(APP_TITLE, f"Gemaakt:\n{bom}\n{net}\n{svg}")
        except Exception as exc:
            messagebox.showerror(APP_TITLE, f"Export mislukt:\n{exc}")

    def _write_bom_csv(self, path: Path):
        path.parent.mkdir(parents=True, exist_ok=True)
        groups: Dict[Tuple[str, float, bool], Dict[str, float]] = {}
        for w in self.wires:
            key = (w.color or "onbekend", w.cross_section_mm2, w.shielded)
            groups.setdefault(key, {"qty": 0, "len": 0.0})
            groups[key]["qty"] += 1
            groups[key]["len"] += w.length_mm
        with path.open("w", newline="", encoding="utf-8") as f:
            wr = csv.writer(f, delimiter=";")
            wr.writerow(["Type", "Ref/ID", "Omschrijving", "Part number", "Aantal", "Totale lengte mm"])
            for c in sorted(self.connectors, key=lambda x: x.ref):
                wr.writerow(["Connector", c.ref, f"{c.name} ({c.pin_count} pin)", c.part_number, 1, ""])
            for i, ((color, mm2, shield), v) in enumerate(sorted(groups.items()), start=1):
                wr.writerow(
                    [
                        "Draad",
                        f"WIRE-GROUP-{i:03d}",
                        f"{color}, {mm2:g} mm2, {'shielded' if shield else 'unshielded'}",
                        "",
                        int(v["qty"]),
                        f"{v['len']:.1f}",
                    ]
                )

    def _write_net_csv(self, path: Path):
        path.parent.mkdir(parents=True, exist_ok=True)
        with path.open("w", newline="", encoding="utf-8") as f:
            wr = csv.writer(f, delimiter=";")
            wr.writerow(
                [
                    "Wire ID",
                    "Signaal",
                    "Van connector",
                    "Van pin",
                    "Naar connector",
                    "Naar pin",
                    "Kleur",
                    "Doorsnede mm2",
                    "Lengte mm",
                    "Shielded",
                ]
            )
            for w in sorted(self.wires, key=lambda x: x.wire_id):
                wr.writerow(
                    [
                        w.wire_id,
                        w.signal_name,
                        w.from_connector,
                        w.from_pin,
                        w.to_connector,
                        w.to_pin,
                        w.color,
                        f"{w.cross_section_mm2:g}",
                        f"{w.length_mm:g}",
                        "Ja" if w.shielded else "Nee",
                    ]
                )

    def _write_svg(self, path: Path):
        width = 1600
        height = 900
        pos = self._connector_positions(width, height)
        alias = self._alias_pin_map()
        by_ref = {c.ref: c for c in self.connectors}
        out = [
            '<?xml version="1.0" encoding="UTF-8"?>',
            f'<svg xmlns="http://www.w3.org/2000/svg" width="{width}" height="{height}" viewBox="0 0 {width} {height}">',
            '<rect x="0" y="0" width="100%" height="100%" fill="#f8fafc"/>',
            '<style>text{font-family:Segoe UI,Arial,sans-serif;fill:#0f172a}.small{font-size:12px}.title{font-size:18px;font-weight:700}</style>',
            f'<text class="title" x="20" y="30">{escape(self.name_var.get() or "Kabelboom")}</text>',
            f'<text class="small" x="20" y="52">Part: {escape(self.part_var.get() or "-")} | Rev: {escape(self.rev_var.get() or "-")}</text>',
        ]
        for c in self.connectors:
            if c.ref not in pos:
                continue
            x, y, bw, bh = pos[c.ref]
            out.append(f'<rect x="{x:.1f}" y="{y:.1f}" width="{bw:.1f}" height="{bh:.1f}" fill="#ffffff" stroke="#334155" stroke-width="2"/>')
            out.append(f'<text class="small" font-weight="700" text-anchor="middle" x="{x + bw / 2:.1f}" y="{y + 18:.1f}">{escape(c.ref)} - {escape(c.name)}</text>')
            out.append(f'<text class="small" text-anchor="middle" x="{x + bw / 2:.1f}" y="{y + bh - 9:.1f}">{c.pin_count} pin | {escape(c.part_number or "-")}</text>')
        for w in self.wires:
            c1 = by_ref.get(w.from_connector)
            c2 = by_ref.get(w.to_connector)
            b1 = pos.get(w.from_connector)
            b2 = pos.get(w.to_connector)
            if not c1 or not c2 or not b1 or not b2:
                continue
            i1 = self._pin_index(c1.ref, w.from_pin, alias)
            i2 = self._pin_index(c2.ref, w.to_pin, alias)
            x1, y1 = self._pin_xy(c1, b1, i1)
            x2, y2 = self._pin_xy(c2, b2, i2)
            mx = (x1 + x2) / 2
            curve = f"M {x1:.1f},{y1:.1f} C {x1 + (mx - x1) * 0.6:.1f},{y1:.1f} {x2 + (mx - x2) * 0.6:.1f},{y2:.1f} {x2:.1f},{y2:.1f}"
            sw = 1.2 + math.sqrt(max(0.05, w.cross_section_mm2)) * 1.2
            out.append(f'<path d="{curve}" fill="none" stroke="{color_hex(w.color)}" stroke-width="{sw:.2f}"/>')
            out.append(f'<text class="small" text-anchor="middle" x="{mx:.1f}" y="{(y1 + y2) / 2 - 6:.1f}">{escape(w.signal_name or w.wire_id)}</text>')
        out.append("</svg>")
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text("\n".join(out), encoding="utf-8")


def main():
    enable_dpi_awareness()
    app = KabelboomStudio()
    app.mainloop()


if __name__ == "__main__":
    main()
