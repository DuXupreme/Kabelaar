#!/usr/bin/env python3
"""Kabelboom Tekenstudio - interactieve 2D harness tekenapp."""

from __future__ import annotations

import sys as _sys

# Velopack roept de exe tijdens (de)installatie en updates aan met
# --veloapp-install/-obsolete/-updated/-uninstall. De app moet die hooks
# herkennen en meteen, zonder venster, afsluiten (binnen 15-30s).
if any(arg.startswith("--veloapp-") for arg in _sys.argv[1:]):
    raise SystemExit(0)

import base64
import csv
import io
import json
import logging
import math
import mimetypes
import random
import re
import tkinter as tk
from dataclasses import asdict, dataclass, field
from pathlib import Path
from tkinter import colorchooser, filedialog, font as tkfont, messagebox, simpledialog, ttk
from typing import Dict, List, Optional, Tuple
from xml.sax.saxutils import escape

import threading

from app_settings import default_settings_path, existing_dir, load_app_settings, parent_dir, update_app_settings
from logging_setup import configure_logging, log_path
from project_io import write_text_atomic
from ui_scaling import UI_SCALE_LABELS, enable_dpi_awareness, normalize_ui_scale_percent, schedule_window_scaling, set_ui_scale
import theme as ui_theme
from ui_tooltip import attach as attach_tooltip
from rendering import RenderingMixin
from io_project import ProjectIOMixin
from ui_panels import UIBuilderMixin, LEFT_PANEL_DEFINITIONS
from geometry import (
    circle_arc_points_3d,
    clamp,
    closest_point_on_segment,
    distance_point_segment,
    envelope_outline,
    normalize_polylines,
    polyline_bbox,
    polyline_point_count,
    polyline_segment_count,
    preview_project,
    project_xyz,
    rotate_xyz,
    segment_intersection,
    step_polyline_segments,
)
from step_import import (
    StepGeometry3D,
    parse_step_geometry,
    parse_step_length_scale,
    parse_step_point_cloud,
    project_step_geometry,
)
from model import (
    BOM_CSV_HEADER,
    ConnectorInstance,
    DEFAULT_WIRE_BRIDGE_CLEARANCE_MM,
    DEFAULT_WIRE_BRIDGE_ENABLED,
    DEFAULT_WIRE_BRIDGE_HEIGHT_MM,
    DEFAULT_WIRE_BRIDGE_LENGTH_MM,
    PROJECT_SCHEMA_VERSION,
    DEFAULT_DIMENSION_ARROW_SIZE_MM,
    DEFAULT_DIMENSION_COLOR,
    DEFAULT_DIMENSION_LINE_WIDTH_MM,
    DEFAULT_DIMENSION_OFFSET_MM,
    DEFAULT_DIMENSION_TEXT_SIZE_PT,
    DEFAULT_LEADER_ARROW_SIZE_MM,
    DEFAULT_LEADER_TEXT_SIZE_PT,
    DEFAULT_PAPER_PRESET,
    DIMENSION_LABEL_TO_ORIENTATION,
    DIMENSION_ORIENTATION_OPTIONS,
    DIMENSION_ORIENTATION_TO_LABEL,
    DIMENSION_ORIENTATIONS,
    DimensionLine,
    ImageNote,
    Leader,
    NETLIST_CSV_HEADER,
    PAPER_PRESET_CUSTOM,
    PAPER_PRESET_OPTIONS,
    PAPER_PRESET_SIZES_MM,
    StepSymbol,
    TableBox,
    TextNote,
    VALID_WIRE_STYLES,
    WIRE_ENDPOINT_DRAG_SCOPE_OPTIONS,
    WIRE_ENDPOINT_DRAG_SCOPE_TO_INTERNAL,
    WIRE_ENDPOINT_DRAG_SCOPE_TO_LABEL,
    WIRE_MOVE_SCOPE_OPTIONS,
    WIRE_MOVE_SCOPE_TO_INTERNAL,
    WIRE_MOVE_SCOPE_TO_LABEL,
    WIRE_STYLE_OPTIONS,
    WIRE_STYLE_TO_INTERNAL,
    WIRE_STYLE_TO_LABEL,
    WirePath,
    connector_pin_label,
    csv_text,
    dimension_orientation_internal,
    dimension_orientation_label,
    normalize_dimension_orientation,
    normalize_wire_style,
    paper_preset_dimensions,
    paper_preset_for_dimensions,
    parse_pin_labels,
    pin_labels_text,
    safe_name,
    try_float,
    wire_bom_rows,
    wire_electrical_drc,
    wire_electrical_kwargs,
    wire_endpoint_drag_scope_internal,
    wire_endpoint_drag_scope_label,
    wire_has_electrical_data,
    wire_move_scope_internal,
    wire_move_scope_label,
    wire_netlist_rows,
    wire_style_internal,
    wire_style_label,
)

try:
    import sv_ttk
except Exception:
    sv_ttk = None

try:
    import updater
except Exception:
    updater = None

try:
    from PIL import Image, ImageDraw, ImageFont, ImageGrab, ImageTk

    PIL_AVAILABLE = True
except Exception:
    Image = None
    ImageDraw = None
    ImageFont = None
    ImageGrab = None
    ImageTk = None
    PIL_AVAILABLE = False

try:
    import cairosvg

    CAIROSVG_AVAILABLE = True
except Exception:
    cairosvg = None
    CAIROSVG_AVAILABLE = False

LOGGER = logging.getLogger("kabelboom")

APP_TITLE = "Kabelboom Tekenstudio"
APP_SETTINGS_KEY = "kabelboom_tekenstudio"
PROJECTION_OPTIONS = [
    "Top (XY)",
    "Bottom (XY)",
    "Front (XZ)",
    "Back (XZ)",
    "Right (YZ)",
    "Left (YZ)",
]
MULTI_SELECTION_TEXT_SENTINEL = "<niet wijzigen>"
CONNECTOR_RASTER_LINE_THRESHOLD = 350
CONNECTOR_RASTER_POINT_THRESHOLD = 900
CONNECTOR_RASTER_MAX_DIM_PX = 4096
CONNECTOR_RASTER_MAX_AREA_PX = 6_000_000


class StepImportDialog(simpledialog.Dialog):
    def __init__(self, parent, default_name: str):
        self.default_name = default_name
        self.result_data: Optional[dict] = None
        super().__init__(parent, title="STEP import instellingen")

    def body(self, master):
        master.columnconfigure(1, weight=1)
        ttk.Label(master, text="Symboolnaam").grid(row=0, column=0, sticky="w", padx=4, pady=4)
        ttk.Label(master, text="Projectiezijde").grid(row=1, column=0, sticky="w", padx=4, pady=4)
        self.name_var = tk.StringVar(value=self.default_name)
        self.proj_var = tk.StringVar(value=PROJECTION_OPTIONS[0])
        ttk.Entry(master, textvariable=self.name_var).grid(row=0, column=1, sticky="ew", padx=4, pady=4)
        ttk.Combobox(master, textvariable=self.proj_var, values=PROJECTION_OPTIONS, state="readonly").grid(
            row=1, column=1, sticky="ew", padx=4, pady=4
        )
        return master

    def validate(self):
        if not self.name_var.get().strip():
            messagebox.showerror(APP_TITLE, "Symboolnaam is verplicht.", parent=self)
            return False
        if self.proj_var.get() not in PROJECTION_OPTIONS:
            messagebox.showerror(APP_TITLE, "Kies een geldige projectiezijde.", parent=self)
            return False
        return True

    def apply(self):
        self.result_data = {
            "name": safe_name(self.name_var.get(), "symbol"),
            "projection": self.proj_var.get(),
        }


class StepPreviewDialog(simpledialog.Dialog):
    ORIENTATION_BUTTONS = [
        ("Top (XY)", "Top +Z"),
        ("Bottom (XY)", "Bottom -Z"),
        ("Front (XZ)", "Front +Y"),
        ("Back (XZ)", "Back -Y"),
        ("Left (YZ)", "Left -X"),
        ("Right (YZ)", "Right +X"),
    ]

    def __init__(self, parent, default_name: str, geometry: StepGeometry3D):
        self.default_name = default_name
        self.geometry = geometry
        self.result_data: Optional[dict] = None
        self.preview_segments = step_polyline_segments(geometry.polylines)
        if len(self.preview_segments) > 15000:
            step = max(1, len(self.preview_segments) // 15000)
            self.preview_segments = self.preview_segments[::step]
        self.yaw = 0.9
        self.pitch = 0.45
        self.zoom = 1.0
        self.drag_last: Optional[Tuple[int, int]] = None
        super().__init__(parent, title="STEP 3D preview en projectie")

    def body(self, master):
        master.columnconfigure(0, weight=1)
        master.rowconfigure(1, weight=1)

        top = ttk.Frame(master)
        top.grid(row=0, column=0, sticky="ew", pady=(0, 6))
        top.columnconfigure(1, weight=1)
        ttk.Label(top, text="Symboolnaam").grid(row=0, column=0, sticky="w", padx=4, pady=2)
        self.name_var = tk.StringVar(value=self.default_name)
        ttk.Entry(top, textvariable=self.name_var).grid(row=0, column=1, sticky="ew", padx=4, pady=2)

        ttk.Label(top, text="Projectiezijde").grid(row=1, column=0, sticky="w", padx=4, pady=2)
        self.proj_var = tk.StringVar(value=PROJECTION_OPTIONS[0])
        self.proj_combo = ttk.Combobox(top, textvariable=self.proj_var, values=PROJECTION_OPTIONS, state="readonly")
        self.proj_combo.grid(row=1, column=1, sticky="ew", padx=4, pady=2)
        self.proj_combo.bind("<<ComboboxSelected>>", lambda _event: self.redraw_preview())

        buttons = ttk.Frame(top)
        buttons.grid(row=2, column=0, columnspan=2, sticky="ew", padx=4, pady=2)
        for idx, (side, label) in enumerate(self.ORIENTATION_BUTTONS):
            ttk.Button(buttons, text=label, command=lambda s=side: (self.proj_var.set(s), self.redraw_preview())).grid(
                row=idx // 3, column=idx % 3, sticky="ew", padx=2, pady=2
            )
            buttons.columnconfigure(idx % 3, weight=1)

        self.canvas = tk.Canvas(master, width=760, height=480, background="#f4f7fb", highlightthickness=1, highlightbackground="#c8d1dd")
        self.canvas.grid(row=1, column=0, sticky="nsew")
        self.canvas.bind("<Button-1>", self._on_down)
        self.canvas.bind("<B1-Motion>", self._on_drag)
        self.canvas.bind("<ButtonRelease-1>", self._on_up)
        self.canvas.bind("<MouseWheel>", self._on_wheel)

        hint = ttk.Label(
            master,
            text="Sleep met linkermuis om 3D te roteren. Muiswiel = zoom. Aslegend: +X=Right, +Y=Front, +Z=Top.",
        )
        hint.grid(row=2, column=0, sticky="ew", pady=(6, 0))

        self.after(20, self.redraw_preview)
        return self.canvas

    def _on_down(self, event):
        self.drag_last = (event.x, event.y)

    def _on_drag(self, event):
        if not self.drag_last:
            return
        dx = event.x - self.drag_last[0]
        dy = event.y - self.drag_last[1]
        self.drag_last = (event.x, event.y)
        self.yaw += dx * 0.01
        self.pitch = clamp(self.pitch + dy * 0.01, -1.5, 1.5)
        self.redraw_preview()

    def _on_up(self, _event):
        self.drag_last = None

    def _on_wheel(self, event):
        factor = 1.1 if event.delta > 0 else 1 / 1.1
        self.zoom = clamp(self.zoom * factor, 0.2, 6.0)
        self.redraw_preview()

    def redraw_preview(self):
        cv = self.canvas
        cv.delete("all")
        w = max(300, cv.winfo_width())
        h = max(220, cv.winfo_height())
        cv.create_rectangle(0, 0, w, h, fill="#f4f7fb", outline="")

        if not self.preview_segments:
            cv.create_text(w / 2, h / 2, text="Geen 3D lijnen gevonden in STEP.", fill="#4a5568")
            return

        projected: List[Tuple[float, float, float, float]] = []
        xs: List[float] = []
        ys: List[float] = []
        for a, b in self.preview_segments:
            ax, ay = preview_project(a, self.yaw, self.pitch)
            bx, by = preview_project(b, self.yaw, self.pitch)
            projected.append((ax, ay, bx, by))
            xs.extend([ax, bx])
            ys.extend([ay, by])

        min_x = min(xs)
        max_x = max(xs)
        min_y = min(ys)
        max_y = max(ys)
        span_x = max(1e-6, max_x - min_x)
        span_y = max(1e-6, max_y - min_y)
        scale = min((w - 32) / span_x, (h - 32) / span_y) * self.zoom
        cx = w / 2.0
        cy = h / 2.0
        ox = (min_x + max_x) / 2.0
        oy = (min_y + max_y) / 2.0

        for ax, ay, bx, by in projected:
            x1 = cx + (ax - ox) * scale
            y1 = cy + (ay - oy) * scale
            x2 = cx + (bx - ox) * scale
            y2 = cy + (by - oy) * scale
            cv.create_line(x1, y1, x2, y2, fill="#1f3d5a", width=1.1)

        axis_origin_x = 72
        axis_origin_y = h - 68
        axis_scale = 32
        for axis_vector, axis_label, axis_color in (
            ((1.0, 0.0, 0.0), "+X Right", "#c92a2a"),
            ((0.0, 1.0, 0.0), "+Y Front", "#2f9e44"),
            ((0.0, 0.0, 1.0), "+Z Top", "#1d4ed8"),
        ):
            vx, vy = preview_project(axis_vector, self.yaw, self.pitch)
            ex = axis_origin_x + vx * axis_scale
            ey = axis_origin_y + vy * axis_scale
            cv.create_line(axis_origin_x, axis_origin_y, ex, ey, fill=axis_color, width=2, arrow=tk.LAST)
            cv.create_text(ex + 6, ey, anchor="w", text=axis_label, fill=axis_color, font=("Segoe UI", 8, "bold"))

        projection_help = {
            "Top (XY)": "Kijkt langs +Z omlaag",
            "Bottom (XY)": "Kijkt langs -Z omhoog",
            "Front (XZ)": "Kijkt langs +Y naar voren",
            "Back (XZ)": "Kijkt langs -Y naar achteren",
            "Left (YZ)": "Kijkt langs -X vanaf links",
            "Right (YZ)": "Kijkt langs +X vanaf rechts",
        }

        cv.create_text(
            10,
            10,
            anchor="nw",
            text=f"Yaw {self.yaw:.2f} | Pitch {self.pitch:.2f} | Segments {len(self.preview_segments)}",
            fill="#334155",
            font=("Segoe UI", 9),
        )
        cv.create_text(
            10,
            28,
            anchor="nw",
            text=f"Gekozen projectie: {self.proj_var.get()} | {projection_help.get(self.proj_var.get(), '')}",
            fill="#334155",
            font=("Segoe UI", 9),
        )

    def validate(self):
        if not self.name_var.get().strip():
            messagebox.showerror(APP_TITLE, "Symboolnaam is verplicht.", parent=self)
            return False
        if self.proj_var.get() not in PROJECTION_OPTIONS:
            messagebox.showerror(APP_TITLE, "Kies een geldige projectiezijde.", parent=self)
            return False
        return True

    def apply(self):
        self.result_data = {
            "name": safe_name(self.name_var.get(), "symbol"),
            "projection": self.proj_var.get(),
        }


class HarnessDrawingStudio(UIBuilderMixin, RenderingMixin, ProjectIOMixin, tk.Tk):
    def __init__(self):
        super().__init__()
        self.title(APP_TITLE)
        self.geometry("1500x920")
        self.settings = load_app_settings(APP_SETTINGS_KEY)
        self._ui_scale_percent = normalize_ui_scale_percent(self.settings.get("ui_scale_percent", 100))
        schedule_window_scaling(self, design_size=(1500, 920), min_size=(1200, 760), apply_tk_scaling=False)

        # Pas het warme thema toe vóór het vastleggen van basis-padding/fonts,
        # zodat de eigen ttk-styling als uitgangspunt wordt gemeten.
        self._app_theme = ui_theme.normalize_theme(self.settings.get("theme", "light"))
        self.app_theme_var = tk.StringVar(value=self._app_theme)
        self._apply_app_theme()

        default_paper = paper_preset_dimensions(DEFAULT_PAPER_PRESET) or (420.0, 297.0)
        self.paper_w_mm = default_paper[0]
        self.paper_h_mm = default_paper[1]
        self.frame_margin_mm = 10.0
        self.banner_h_mm = 48.0
        self.banner_w_mm = 190.0

        self.zoom = 2.5  # px per mm
        self.pan_x = 120.0
        self.pan_y = 80.0

        self.project_path: Optional[Path] = None
        self._saved_snapshot = ""
        self.symbols: Dict[str, StepSymbol] = {}
        self.connectors: List[ConnectorInstance] = []
        self.wires: List[WirePath] = []
        self.leaders: List[Leader] = []
        self.dimensions: List[DimensionLine] = []
        self.text_notes: List[TextNote] = []
        self.image_notes: List[ImageNote] = []
        self.tables: List[TableBox] = []

        self.default_wire_color = "#1f4e79"
        self.default_wire_color_b = "#d7263d"
        self.default_wire_width_mm = 1.2
        self.default_wire_style = "straight"
        self.default_wire_curve_offset_mm = 8.0
        self.default_wire_twist_pitch_mm = 10.0
        self.default_wire_pair_gap_mm = 2.8
        self.default_leader_color = "#1e3a5f"
        self.default_leader_width_mm = 0.7
        self.default_leader_arrow_size_mm = DEFAULT_LEADER_ARROW_SIZE_MM
        self.default_leader_text_size_pt = DEFAULT_LEADER_TEXT_SIZE_PT
        self.default_leader_text_box = False
        self.default_dimension_color = DEFAULT_DIMENSION_COLOR
        self.default_dimension_line_width_mm = DEFAULT_DIMENSION_LINE_WIDTH_MM
        self.default_dimension_arrow_size_mm = DEFAULT_DIMENSION_ARROW_SIZE_MM
        self.default_dimension_text_size_pt = DEFAULT_DIMENSION_TEXT_SIZE_PT
        self.default_dimension_offset_mm = DEFAULT_DIMENSION_OFFSET_MM
        self.default_dimension_orientation = "horizontal"
        self.default_dimension_tolerance = ""
        self.default_connector_line_color = "#2a3550"
        self.default_connector_line_width_mm = 0.6
        self.default_table_border_color = "#25364a"
        self.default_table_border_width_mm = 0.5
        self.wire_bridge_enabled = DEFAULT_WIRE_BRIDGE_ENABLED
        self.wire_bridge_height_mm = DEFAULT_WIRE_BRIDGE_HEIGHT_MM
        self.wire_bridge_length_mm = DEFAULT_WIRE_BRIDGE_LENGTH_MM
        self.wire_bridge_clearance_mm = DEFAULT_WIRE_BRIDGE_CLEARANCE_MM

        self.mode = "select"  # select|place_connector|draw_wire|draw_leader|draw_table
        self.active_symbol: Optional[str] = None
        self.selected: Optional[Tuple[str, str]] = None  # (kind, id)
        self.selected_items: set[Tuple[str, str]] = set()
        self.selected_wire_ids: set[str] = set()
        self.box_select_state: Optional[dict] = None
        self.drag_start_world: Optional[Tuple[float, float]] = None
        self.drag_original_world: Optional[Tuple[float, float]] = None
        self.drag_original_points: Optional[List[Tuple[float, float]]] = None
        self.drag_original_wire_points: Optional[Dict[str, List[Tuple[float, float]]]] = None
        self.drag_original_items: Optional[Dict[Tuple[str, str], object]] = None
        self.wire_endpoint_drag_state: Optional[dict] = None
        self.leader_endpoint_drag_state: Optional[dict] = None
        self.wire_tangent_drag_state: Optional[dict] = None
        self.wire_curve_drag_state: Optional[dict] = None
        self.table_resize_state: Optional[dict] = None
        self.connector_label_drag_state: Optional[dict] = None
        self.panning = False
        self.pan_start: Optional[Tuple[float, float]] = None
        self.cursor_world: Tuple[float, float] = (0.0, 0.0)
        self._last_preview_cursor_world: Optional[Tuple[float, float]] = None

        self.temp_wire_points: List[Tuple[float, float]] = []
        self.temp_wire_anchor_meta: Optional[dict] = None
        self.temp_wire_segment_history: List[dict] = []
        self.temp_leader_start: Optional[Tuple[float, float]] = None
        self.temp_dimension_start: Optional[Tuple[float, float]] = None
        self.temp_table_start: Optional[Tuple[float, float]] = None

        self.project_name_var = tk.StringVar(value="Nieuwe kabelboom tekening")
        self.rev_var = tk.StringVar(value="A")
        self.engineer_var = tk.StringVar(value="")
        self.drawing_number_var = tk.StringVar(value="")
        self.customer_var = tk.StringVar(value="")
        self.checked_by_var = tk.StringVar(value="")
        self.approved_by_var = tk.StringVar(value="")
        self.date_drawn_var = tk.StringVar(value="")
        self.date_checked_var = tk.StringVar(value="")
        self.date_approved_var = tk.StringVar(value="")
        self.scale_text_var = tk.StringVar(value="NTS")
        self.sheet_var = tk.StringVar(value="1 OF 1")
        self.unit_var = tk.StringVar(value="mm")
        self.tol_x_var = tk.StringVar(value="±0.25")
        self.tol_xx_var = tk.StringVar(value="±0.1")
        self.tol_xxx_var = tk.StringVar(value="±0.05")
        self.paper_preset_var = tk.StringVar(value=DEFAULT_PAPER_PRESET)
        self.status_var = tk.StringVar(value="Klaar")
        self.coord_var = tk.StringVar(value="x= -  y= -")
        self.perf_var = tk.StringVar(value="")
        self.perf_meter_var = tk.BooleanVar(value=False)
        self.mode_var = tk.StringVar(value="Mode: Select")
        self.snap_grid_enabled_var = tk.BooleanVar(value=True)
        self.snap_grid_mm_var = tk.StringVar(value="1.0")
        self.snap_endpoint_enabled_var = tk.BooleanVar(value=True)
        self.wire_bridge_enabled_var = tk.BooleanVar(value=self.wire_bridge_enabled)
        self.wire_bridge_height_var = tk.StringVar(value=f"{self.wire_bridge_height_mm:g}")
        self.wire_bridge_length_var = tk.StringVar(value=f"{self.wire_bridge_length_mm:g}")
        self.wire_bridge_clearance_var = tk.StringVar(value=f"{self.wire_bridge_clearance_mm:g}")
        self.ui_scale_var = tk.StringVar(value=f"{self._ui_scale_percent}%")
        self.prop_wire_move_scope_var = tk.StringVar(value=wire_move_scope_label("chain"))
        self.prop_wire_endpoint_drag_scope_var = tk.StringVar(value=wire_endpoint_drag_scope_label("single"))
        self.wire_move_scope = "chain"
        self.wire_endpoint_drag_scope = "single"
        self.side_panel_visible_var = tk.BooleanVar(value=self._settings_bool("side_panel_visible", True))
        visible_panels = self.settings.get("visible_panels", {})
        collapsed_panels = self.settings.get("collapsed_panels", {})
        if not isinstance(visible_panels, dict):
            visible_panels = {}
        if not isinstance(collapsed_panels, dict):
            collapsed_panels = {}
        self.panel_visible_vars = {
            key: tk.BooleanVar(value=self._settings_bool_from(visible_panels, key, True)) for key, _label in LEFT_PANEL_DEFINITIONS
        }
        self.panel_collapsed_vars = {
            key: tk.BooleanVar(value=self._settings_bool_from(collapsed_panels, key, False)) for key, _label in LEFT_PANEL_DEFINITIONS
        }
        self.side_panel_toggle_text_var = tk.StringVar(value="")
        raw_side_panel_width = self.settings.get("side_panel_width_px")
        self._side_panel_width_user_set = raw_side_panel_width is not None
        self.side_panel_width_px = self._normalize_side_panel_width(
            raw_side_panel_width if self._side_panel_width_user_set else self._default_side_panel_width()
        )
        self._side_panel_drag_start_x: Optional[int] = None
        self._side_panel_drag_start_width: Optional[int] = None

        self._history_undo: List[str] = []
        self._history_redo: List[str] = []
        self._history_replaying = False
        self._history_limit = 120
        self._drag_history_before: Optional[str] = None
        self._drag_history_changed = False
        self._connector_world_cache: Dict[str, dict] = {}
        self._connector_canvas_cache: Dict[str, dict] = {}
        self._connector_local_cache: Dict[str, dict] = {}
        self._connector_image_cache: Dict[str, dict] = {}
        self._connector_canvas_view_signature: Optional[Tuple] = None
        self._wire_centerline_cache: Dict[Tuple, List[Tuple[float, float]]] = {}
        self._wire_polyline_cache: Dict[Tuple, List[List[Tuple[float, float]]]] = {}
        self._wire_bridge_signature: Optional[Tuple] = None
        self._wire_bridge_cache: Dict[str, List[dict]] = {}
        self._wire_connectivity_signature: Optional[Tuple] = None
        self._wire_connectivity_cache: Dict[str, set[str]] = {}
        self._wire_segment_index_signature: Optional[Tuple] = None
        self._wire_hit_segment_index: Dict[Tuple[int, int], List[dict]] = {}
        self._wire_snap_segment_index: Dict[Tuple[int, int], List[dict]] = {}
        self._snap_candidate_signature: Optional[Tuple] = None
        self._snap_candidate_cache: List[Tuple[float, float]] = []
        self._image_canvas_cache: Dict[str, dict] = {}
        self._canvas_image_refs: List[object] = []
        self._pil_font_cache: Dict[Tuple[int, bool], object] = {}
        self._aa_scene_cache = None
        self._aa_scene_dirty = True
        self._aa_model_signature = None
        self._aa_viewport_photo = None
        self._aa_viewport_pil = None
        self._aa_zoom_preview = False
        self._aa_zoom_settle_after_id = None
        self._aa_render_error = ""
        self._redraw_interval_ms = 16
        self._redraw_scheduled = False
        self._redraw_after_id = None
        self._show_perf_meter = False
        self._last_redraw_ms = 0.0
        self._redraw_ms_samples: List[float] = []
        # Incrementeel slepen: tijdens een object-drag wordt de achtergrond bevroren en
        # alleen het versleepte object per beweging hertekend (zie _begin_incremental_drag).
        self._drag_filter = None
        self._drag_incremental = False
        self._drag_active_ids = None
        self._drag_bg_image_refs: List[object] = []
        self._panel_sections: Dict[str, dict] = {}
        self._property_row_widgets: Dict[int, List[object]] = {}
        self._mode_buttons: Dict[str, List[ttk.Button]] = {}
        self._base_named_font_sizes: Dict[str, int] = {}
        self._style_base_padding: Dict[str, object] = {}
        self._init_control_ui_scaling()

        self._build_ui()
        self._build_menubar()
        self._apply_panel_layout(persist=False)
        self._apply_control_ui_scale()
        self._refresh_block_list()
        self._merge_library_symbols_into_session()
        self.load_selection_properties_to_panel()
        self._bind_events()
        self._reset_history()
        self.redraw()
        self._mark_saved()
        self.protocol("WM_DELETE_WINDOW", self._on_close)

        self._update_in_progress = False
        # Stille controle op updates kort na het opstarten.
        if updater is not None and updater.is_update_supported():
            self.after(3000, lambda: self._start_update_check(silent=True))

    def check_for_updates_interactive(self):
        """Menu 'Zoek naar updates...': handmatige controle met terugkoppeling."""
        if updater is None or not updater.is_update_supported():
            messagebox.showinfo(
                "Updates",
                "Automatisch bijwerken is alleen beschikbaar in de "
                "geinstalleerde versie van de app.",
            )
            return
        self._start_update_check(silent=False)

    def _start_update_check(self, silent: bool):
        if updater is None or getattr(self, "_update_in_progress", False):
            return

        def worker():
            info, error = None, None
            try:
                info = updater.check_for_updates()
            except Exception as exc:  # netwerk-/feedfout
                error = exc
            self.after(0, lambda: self._on_update_check_done(info, error, silent))

        threading.Thread(target=worker, daemon=True).start()

    def _on_update_check_done(self, info, error, silent: bool):
        if error is not None:
            if not silent:
                messagebox.showwarning(
                    "Updates", f"Kon niet op updates controleren:\n{error}"
                )
            return
        if info is None:
            if not silent:
                messagebox.showinfo("Updates", "Je gebruikt de nieuwste versie.")
            return
        if messagebox.askyesno(
            "Update beschikbaar",
            f"Versie {info.version} is beschikbaar.\n"
            "Nu downloaden en installeren? De app wordt daarna opnieuw gestart.",
        ):
            self._run_update(info)

    def _run_update(self, info):
        self._update_in_progress = True
        win = tk.Toplevel(self)
        win.title("Bijwerken")
        win.transient(self)
        win.resizable(False, False)
        win.protocol("WM_DELETE_WINDOW", lambda: None)  # niet sluiten tijdens download
        ttk.Label(
            win, text=f"Versie {info.version} wordt gedownload..."
        ).pack(padx=24, pady=(18, 10))
        bar = ttk.Progressbar(win, length=340, mode="determinate", maximum=100)
        bar.pack(padx=24, pady=(0, 18))
        win.update_idletasks()

        def progress(done, total):
            pct = int(done * 100 / total) if total else 0
            self.after(0, lambda: bar.configure(value=pct))

        def worker():
            try:
                pkg = updater.download_package(info, progress)
                updater.apply_and_restart(pkg)
            except Exception as exc:
                self.after(0, lambda: self._update_failed(win, exc))
                return
            self.after(0, self.destroy)  # Update.exe wacht op afsluiten en herstart

        threading.Thread(target=worker, daemon=True).start()

    def _update_failed(self, win, exc):
        self._update_in_progress = False
        try:
            win.destroy()
        except Exception:
            pass
        messagebox.showerror(
            "Bijwerken mislukt", f"Kon de update niet voltooien:\n{exc}"
        )

    def _bind_events(self):
        self.canvas.bind("<Button-1>", self.on_left_down)
        self.canvas.bind("<B1-Motion>", self.on_left_drag)
        self.canvas.bind("<ButtonRelease-1>", self.on_left_up)
        self.canvas.bind("<Double-Button-1>", self.on_double_click)
        self.canvas.bind("<Button-2>", self.on_middle_down)
        self.canvas.bind("<B2-Motion>", self.on_middle_drag)
        self.canvas.bind("<ButtonRelease-2>", self.on_middle_up)
        self.canvas.bind("<Motion>", self.on_motion)
        self.canvas.bind("<MouseWheel>", self.on_mouse_wheel)
        self.canvas.bind("<Button-3>", self.on_right_click)

        self.bind("<Return>", lambda _e: self.finish_wire())
        self.bind("<Escape>", self.on_escape_key)
        self.bind_all("<Control-s>", self._on_shortcut_save)
        self.bind_all("<Control-Shift-S>", self._on_shortcut_save_as)
        self.bind_all("<Control-o>", self._on_shortcut_open)
        self.bind_all("<Control-n>", self._on_shortcut_new)
        self.bind_all("<Control-z>", self.undo)
        self.bind_all("<Control-y>", self.redo)
        self.bind_all("<Control-v>", self.paste_from_clipboard)
        self.bind_all("<Delete>", self._on_shortcut_delete)
        self.bind_all("<Control-d>", self._on_shortcut_duplicate)
        self.bind_all("<F1>", lambda _e: self.show_shortcuts_dialog())

    # ---------------- Reusable blocks ----------------
    def _blocks_library_path(self) -> Path:
        return default_settings_path().parent / "blocks_library.json"

    def _load_blocks_library(self) -> List[dict]:
        try:
            data = json.loads(self._blocks_library_path().read_text(encoding="utf-8"))
        except (OSError, json.JSONDecodeError):
            return []
        if isinstance(data, dict):
            data = data.get("blocks", [])
        if not isinstance(data, list):
            return []
        return [b for b in data if isinstance(b, dict) and b.get("name")]

    def _save_blocks_library(self, blocks: List[dict]):
        path = self._blocks_library_path()
        path.parent.mkdir(parents=True, exist_ok=True)
        write_text_atomic(path, json.dumps({"blocks": blocks}, ensure_ascii=False, indent=2), backup=False)

    # ---------------- Persistent symbol library ----------------
    def _symbols_library_path(self) -> Path:
        return default_settings_path().parent / "symbols_library.json"

    def _load_symbols_library(self) -> List[dict]:
        try:
            data = json.loads(self._symbols_library_path().read_text(encoding="utf-8"))
        except (OSError, json.JSONDecodeError):
            return []
        if isinstance(data, dict):
            data = data.get("symbols", [])
        if not isinstance(data, list):
            return []
        return [s for s in data if isinstance(s, dict) and s.get("name")]

    def _save_symbols_library(self, symbols: List[dict]):
        path = self._symbols_library_path()
        path.parent.mkdir(parents=True, exist_ok=True)
        write_text_atomic(path, json.dumps({"symbols": symbols}, ensure_ascii=False, indent=2), backup=False)

    def _persist_symbol_to_library(self, sym: StepSymbol):
        """Bewaar een geïmporteerd symbool zodat het tussen sessies/projecten beschikbaar blijft."""
        try:
            stored = [s for s in self._load_symbols_library() if s.get("name") != sym.name]
            stored.append(asdict(sym))
            self._save_symbols_library(stored)
        except OSError:
            pass

    def _merge_library_symbols_into_session(self):
        """Voeg opgeslagen bibliotheeksymbolen toe aan de huidige sessie (zonder bestaande te overschrijven)."""
        added = False
        for sym_d in self._load_symbols_library():
            name = sym_d.get("name")
            if not name or name in self.symbols:
                continue
            try:
                self.symbols[name] = StepSymbol(
                    name=str(sym_d["name"]),
                    source_path=str(sym_d.get("source_path", "")),
                    projection=str(sym_d.get("projection", "Top (XY)")),
                    polylines=[[(float(x), float(y)) for x, y in line] for line in sym_d.get("polylines", [])],
                    width_mm=float(sym_d.get("width_mm", 10.0)),
                    height_mm=float(sym_d.get("height_mm", 10.0)),
                )
                added = True
            except Exception:
                continue
        if added and hasattr(self, "symbol_list"):
            self._refresh_symbol_list()

    def _refresh_block_list(self, select: Optional[str] = None):
        if not hasattr(self, "block_combo"):
            return
        names = [b["name"] for b in self._load_blocks_library()]
        try:
            self.block_combo.configure(values=names)
        except tk.TclError:
            return
        if select and select in names:
            self.block_name_var.set(select)
        elif names and self.block_name_var.get() not in names:
            self.block_name_var.set(names[0])
        elif not names:
            self.block_name_var.set("")

    def _build_block_from_selection(self, name: str, items: set) -> Optional[dict]:
        boxes = [self._item_bbox(k, i) for (k, i) in items if self._item_bbox(k, i)]
        if not boxes:
            return None
        minx = min(b[0] for b in boxes)
        miny = min(b[1] for b in boxes)

        def sp(point) -> List[float]:
            return [point[0] - minx, point[1] - miny]

        connectors, wires, leaders, dimensions, tables, texts = [], [], [], [], [], []
        symbol_names: set[str] = set()
        for c in self.connectors:
            if ("connector", c.connector_id) in items:
                d = asdict(c)
                d["x_mm"] -= minx
                d["y_mm"] -= miny
                connectors.append(d)
                symbol_names.add(c.symbol_name)
        for w in self.wires:
            if ("wire", w.wire_id) in items:
                d = asdict(w)
                d["points_mm"] = [sp(p) for p in w.points_mm]
                wires.append(d)
        for l in self.leaders:
            if ("leader", l.leader_id) in items:
                d = asdict(l)
                d["start_mm"] = sp(l.start_mm)
                d["end_mm"] = sp(l.end_mm)
                leaders.append(d)
        for dim in self.dimensions:
            if ("dimension", dim.dim_id) in items:
                d = asdict(dim)
                d["p1_mm"] = sp(dim.p1_mm)
                d["p2_mm"] = sp(dim.p2_mm)
                dimensions.append(d)
        for t in self.tables:
            if ("table", t.table_id) in items:
                d = asdict(t)
                d["x_mm"] -= minx
                d["y_mm"] -= miny
                tables.append(d)
        for n in self.text_notes:
            if ("text", n.note_id) in items:
                d = asdict(n)
                d["x_mm"] -= minx
                d["y_mm"] -= miny
                texts.append(d)
        symbols = [asdict(self.symbols[s]) for s in symbol_names if s in self.symbols]
        count = len(connectors) + len(wires) + len(leaders) + len(dimensions) + len(tables) + len(texts)
        return {
            "name": name,
            "count": count,
            "symbols": symbols,
            "objects": {
                "connectors": connectors,
                "wires": wires,
                "leaders": leaders,
                "dimensions": dimensions,
                "tables": tables,
                "text_notes": texts,
            },
        }

    def save_selection_as_block(self):
        block_kinds = {"connector", "wire", "leader", "dimension", "table", "text"}
        items = {(k, i) for (k, i) in self._active_selected_items() if k in block_kinds}
        if not items:
            self._show_warning("Selecteer eerst objecten (connector, draad, leader, maatlijn, tabel of tekst) om als blok op te slaan.")
            return
        name = self._ask_string("Naam voor blok:", initialvalue="")
        if not name or not name.strip():
            return
        name = name.strip()
        block = self._build_block_from_selection(name, items)
        if not block:
            self._show_warning("Kon geen blok maken van de selectie.")
            return
        blocks = [b for b in self._load_blocks_library() if b.get("name") != name]
        blocks.append(block)
        try:
            self._save_blocks_library(blocks)
        except OSError as exc:
            self._show_error(f"Blok opslaan mislukt:\n{exc}")
            return
        self._refresh_block_list(select=name)
        self.status(f"Blok '{name}' opgeslagen ({block['count']} objecten).")

    def insert_selected_block(self):
        name = self.block_name_var.get().strip()
        if not name:
            self._show_warning("Geen blok geselecteerd. Maak eerst een blok via 'Selectie → blok'.")
            return
        block = next((b for b in self._load_blocks_library() if b.get("name") == name), None)
        if not block:
            self._show_warning(f"Blok '{name}' niet gevonden.")
            return
        self._insert_block(block)

    def delete_selected_block(self):
        name = self.block_name_var.get().strip()
        if not name:
            return
        blocks = [b for b in self._load_blocks_library() if b.get("name") != name]
        self._save_blocks_library(blocks)
        self._refresh_block_list()
        self.status(f"Blok '{name}' verwijderd.")

    def _insert_block(self, block: dict):
        objects = block.get("objects", {})
        ox, oy = self._default_insert_world()
        before = self._capture_before_change()

        for sym_d in block.get("symbols", []):
            try:
                sym_name = sym_d.get("name")
                if sym_name and sym_name not in self.symbols:
                    self.symbols[sym_name] = StepSymbol(**sym_d)
            except Exception:
                continue

        new_items: set = set()
        conn_id_map: Dict[str, str] = {}
        for d in objects.get("connectors", []):
            nid = self._next_id("J", [c.connector_id for c in self.connectors])
            conn_id_map[str(d.get("connector_id"))] = nid
            try:
                c = ConnectorInstance(**{**d, "connector_id": nid})
            except Exception:
                continue
            c.x_mm += ox
            c.y_mm += oy
            c.pin_labels = list(c.pin_labels or [])
            self.connectors.append(c)
            new_items.add(("connector", nid))

        for d in objects.get("wires", []):
            nid = self._next_id("W", [w.wire_id for w in self.wires])
            try:
                data = {**d, "wire_id": nid}
                data["points_mm"] = [(p[0] + ox, p[1] + oy) for p in d.get("points_mm", [])]
                if data.get("from_connector") in conn_id_map:
                    data["from_connector"] = conn_id_map[data["from_connector"]]
                if data.get("to_connector") in conn_id_map:
                    data["to_connector"] = conn_id_map[data["to_connector"]]
                data["start_handle_offset_mm"] = tuple(d.get("start_handle_offset_mm", (0.0, 0.0)))
                data["end_handle_offset_mm"] = tuple(d.get("end_handle_offset_mm", (0.0, 0.0)))
                w = WirePath(**data)
            except Exception:
                continue
            self.wires.append(w)
            new_items.add(("wire", nid))

        for d in objects.get("leaders", []):
            nid = self._next_id("L", [l.leader_id for l in self.leaders])
            try:
                data = {**d, "leader_id": nid}
                data["start_mm"] = (d["start_mm"][0] + ox, d["start_mm"][1] + oy)
                data["end_mm"] = (d["end_mm"][0] + ox, d["end_mm"][1] + oy)
                self.leaders.append(Leader(**data))
            except Exception:
                continue
            new_items.add(("leader", nid))

        for d in objects.get("dimensions", []):
            nid = self._next_id("D", [dim.dim_id for dim in self.dimensions])
            try:
                data = {**d, "dim_id": nid}
                data["p1_mm"] = (d["p1_mm"][0] + ox, d["p1_mm"][1] + oy)
                data["p2_mm"] = (d["p2_mm"][0] + ox, d["p2_mm"][1] + oy)
                self.dimensions.append(DimensionLine(**data))
            except Exception:
                continue
            new_items.add(("dimension", nid))

        for d in objects.get("tables", []):
            nid = self._next_id("T", [t.table_id for t in self.tables])
            try:
                table = TableBox(**{**d, "table_id": nid})
            except Exception:
                continue
            table.x_mm += ox
            table.y_mm += oy
            self.tables.append(table)
            new_items.add(("table", nid))

        for d in objects.get("text_notes", []):
            nid = self._next_id("N", [n.note_id for n in self.text_notes])
            try:
                note = TextNote(**{**d, "note_id": nid})
            except Exception:
                continue
            note.x_mm += ox
            note.y_mm += oy
            self.text_notes.append(note)
            new_items.add(("text", nid))

        self._clear_connector_caches()
        self._clear_wire_caches()
        self._refresh_symbol_list()
        if new_items:
            self._set_selected_items(new_items, primary=sorted(new_items, key=self._selection_sort_key)[0])
            self.load_selection_properties_to_panel()
        self.redraw()
        self._commit_change(before, f"Blok '{block.get('name', '?')}' ingevoegd ({len(new_items)} objecten).")

    def _default_insert_world(self) -> Tuple[float, float]:
        if self.canvas.winfo_exists():
            cx = self.canvas.winfo_width() / 2.0
            cy = self.canvas.winfo_height() / 2.0
            return self.canvas_to_world(cx, cy)
        return self.cursor_world

    def _image_note_size_mm(self, px_w: int, px_h: int, max_side_mm: float = 70.0) -> Tuple[float, float]:
        px_w = max(1, int(px_w))
        px_h = max(1, int(px_h))
        scale = min(max_side_mm / px_w, max_side_mm / px_h)
        return (max(8.0, px_w * scale), max(8.0, px_h * scale))

    def _png_b64_from_pil(self, image) -> str:
        if not PIL_AVAILABLE or image is None:
            return ""
        buffer = io.BytesIO()
        image.save(buffer, format="PNG")
        return base64.b64encode(buffer.getvalue()).decode("ascii")

    def _image_note_bytes(self, note: ImageNote) -> Optional[bytes]:
        if note.image_data_b64:
            try:
                return base64.b64decode(note.image_data_b64)
            except Exception:
                return None
        if note.source_path:
            try:
                return Path(note.source_path).read_bytes()
            except Exception:
                return None
        return None

    def _visible_world_bounds(self) -> Tuple[float, float, float, float]:
        x1, y1 = self.canvas_to_world(0, 0)
        x2, y2 = self.canvas_to_world(self.canvas.winfo_width(), self.canvas.winfo_height())
        return (min(x1, x2), min(y1, y2), max(x1, x2), max(y1, y2))

    def _text_note_bbox(self, note: TextNote) -> Tuple[float, float, float, float]:
        font_mm = max(1.8, float(note.font_size_pt) * 0.352778)
        lines = str(note.text or "").replace("\r\n", "\n").split("\n")
        if not lines:
            lines = [""]
        max_chars = max(1, max(len(line) for line in lines))
        width_mm = max(5.0, font_mm * 0.62 * max_chars)
        height_mm = max(font_mm * 1.2, len(lines) * font_mm * 1.25)
        return (note.x_mm, note.y_mm, note.x_mm + width_mm, note.y_mm + height_mm)

    def _image_note_display_size(self, note: ImageNote) -> Tuple[float, float]:
        return (
            max(2.0, note.width_mm * max(0.05, note.scale)),
            max(2.0, note.height_mm * max(0.05, note.scale)),
        )

    def _image_note_bbox(self, note: ImageNote) -> Tuple[float, float, float, float]:
        width_mm, height_mm = self._image_note_display_size(note)
        return (note.x_mm, note.y_mm, note.x_mm + width_mm, note.y_mm + height_mm)

    def _canvas_photo_for_image_note(self, note: ImageNote):
        image_bytes = self._image_note_bytes(note)
        if not image_bytes:
            return None
        width_mm, height_mm = self._image_note_display_size(note)
        pixel_w = max(1, int(round(width_mm * self.zoom)))
        pixel_h = max(1, int(round(height_mm * self.zoom)))
        signature = (
            round(note.scale, 5),
            round(width_mm, 4),
            round(height_mm, 4),
            pixel_w,
            pixel_h,
            len(image_bytes),
        )
        cached = self._image_canvas_cache.get(note.image_id)
        if cached and cached.get("signature") == signature:
            return cached.get("photo")

        photo = None
        if PIL_AVAILABLE and ImageTk is not None:
            try:
                with Image.open(io.BytesIO(image_bytes)) as img:
                    resampling = getattr(getattr(Image, "Resampling", Image), "LANCZOS", Image.LANCZOS)
                    rendered = img.convert("RGBA").resize((pixel_w, pixel_h), resampling)
                    photo = ImageTk.PhotoImage(rendered)
            except Exception:
                photo = None
        self._image_canvas_cache[note.image_id] = {"signature": signature, "photo": photo}
        return photo

    def _svg_image_href(self, note: ImageNote) -> str:
        image_bytes = self._image_note_bytes(note)
        if not image_bytes:
            return ""
        mime_type = note.mime_type or "image/png"
        if PIL_AVAILABLE and mime_type not in {"image/png", "image/jpeg", "image/gif"}:
            try:
                with Image.open(io.BytesIO(image_bytes)) as img:
                    return f"data:image/png;base64,{self._png_b64_from_pil(img.convert('RGBA'))}"
            except Exception:
                pass
        return f"data:{mime_type};base64,{base64.b64encode(image_bytes).decode('ascii')}"

    def _svg_multiline_text(self, x_mm: float, y_mm: float, text: str, fill: str, font_size_pt: float) -> List[str]:
        lines = str(text or "").replace("\r\n", "\n").split("\n")
        if not lines:
            lines = [""]
        font_mm = max(1.8, float(font_size_pt) * 0.352778)
        line_step = font_mm * 1.25
        out: List[str] = []
        for idx, line in enumerate(lines):
            content = escape(line) if line else "&#160;"
            out.append(
                f'<text x="{x_mm:.3f}" y="{y_mm + idx * line_step:.3f}" text-anchor="start" dominant-baseline="hanging" '
                f'fill="{escape(fill)}" font-family="Segoe UI,Arial,sans-serif" font-size="{font_mm:.3f}mm">{content}</text>'
            )
        return out

    def _create_text_note(self, text: str, point: Optional[Tuple[float, float]] = None):
        text = str(text).replace("\r\n", "\n").strip()
        if not text:
            return
        point = point or self._default_insert_world()
        before = self._capture_before_change()
        note_id = self._next_id("N", [n.note_id for n in self.text_notes])
        self.text_notes.append(
            TextNote(
                note_id=note_id,
                x_mm=point[0],
                y_mm=point[1],
                text=text,
                color="#1f2937",
                font_size_pt=10.0,
            )
        )
        self._set_single_selection("text", note_id)
        self.load_selection_properties_to_panel()
        self.redraw()
        self._commit_change(before, f"Tekst {note_id} geplakt.")

    def _create_image_note_from_bytes(
        self,
        image_bytes: bytes,
        mime_type: str = "image/png",
        source_path: str = "",
        point: Optional[Tuple[float, float]] = None,
    ) -> bool:
        point = point or self._default_insert_world()
        width_mm = 70.0
        height_mm = 50.0
        if PIL_AVAILABLE and image_bytes:
            try:
                with Image.open(io.BytesIO(image_bytes)) as img:
                    width_mm, height_mm = self._image_note_size_mm(*img.size)
                    image_bytes = base64.b64decode(self._png_b64_from_pil(img.convert("RGBA")))
                    mime_type = "image/png"
            except Exception:
                pass
        before = self._capture_before_change()
        image_id = self._next_id("I", [n.image_id for n in self.image_notes])
        self.image_notes.append(
            ImageNote(
                image_id=image_id,
                x_mm=point[0],
                y_mm=point[1],
                width_mm=width_mm,
                height_mm=height_mm,
                scale=1.0,
                source_path=source_path,
                image_data_b64=base64.b64encode(image_bytes).decode("ascii") if image_bytes else "",
                mime_type=mime_type or "image/png",
            )
        )
        self._set_single_selection("image", image_id)
        self.load_selection_properties_to_panel()
        self.redraw()
        self._commit_change(before, f"Afbeelding {image_id} toegevoegd.")
        return True

    def import_image_note(self):
        path = self._ask_open_filename(
            title="Afbeelding importeren",
            filetypes=[
                ("Afbeeldingen", "*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff;*.webp"),
                ("Alle bestanden", "*.*"),
            ],
            settings_key="last_import_dir",
        )
        if not path:
            return
        try:
            image_bytes = Path(path).read_bytes()
            mime_type = mimetypes.guess_type(path)[0] or "image/png"
            self._create_image_note_from_bytes(image_bytes, mime_type=mime_type, source_path=str(path))
        except Exception as exc:
            self._show_error(f"Afbeelding importeren mislukt:\n{exc}")

    def paste_from_clipboard(self, _event=None):
        inserted = False
        if PIL_AVAILABLE and ImageGrab is not None:
            try:
                clip = ImageGrab.grabclipboard()
            except Exception:
                clip = None
            if clip is not None:
                if hasattr(clip, "save"):
                    inserted = self._create_image_note_from_bytes(base64.b64decode(self._png_b64_from_pil(clip)), "image/png")
                elif isinstance(clip, list):
                    for entry in clip:
                        try:
                            path = Path(entry)
                        except Exception:
                            continue
                        if path.suffix.lower() in {".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff", ".webp"} and path.exists():
                            try:
                                inserted = self._create_image_note_from_bytes(
                                    path.read_bytes(),
                                    mime_type=(mimetypes.guess_type(str(path))[0] or "image/png"),
                                    source_path=str(path),
                                )
                            except Exception:
                                inserted = False
                            if inserted:
                                break
        if inserted:
            return "break"

        try:
            clip_text = self.clipboard_get()
        except tk.TclError:
            clip_text = ""
        clip_text = str(clip_text or "")
        candidate = clip_text.strip().strip('"')
        if candidate:
            candidate_path = Path(candidate)
            if candidate_path.exists() and candidate_path.suffix.lower() in {".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff", ".webp"}:
                try:
                    inserted = self._create_image_note_from_bytes(
                        candidate_path.read_bytes(),
                        mime_type=(mimetypes.guess_type(str(candidate_path))[0] or "image/png"),
                        source_path=str(candidate_path),
                    )
                except Exception:
                    inserted = False
            else:
                self._create_text_note(clip_text)
                inserted = True
        if not inserted:
            self.status("Geen plakbare tekst of afbeelding gevonden in het klembord.")
        return "break"

    def _banner_rect_mm(self) -> Tuple[float, float, float, float]:
        f = self.frame_margin_mm
        bx2 = self.paper_w_mm - f
        by2 = self.paper_h_mm - f
        bx1 = bx2 - self.banner_w_mm
        by1 = by2 - self.banner_h_mm
        return (bx1, by1, bx2, by2)

    def _paper_size_label(self) -> str:
        preset = paper_preset_for_dimensions(self.paper_w_mm, self.paper_h_mm)
        match = re.search(r"A\d", preset)
        if match:
            return match.group(0)
        # Fallback: classify by long edge.
        long_edge = max(self.paper_w_mm, self.paper_h_mm)
        for label, edge in (("A0", 1189), ("A1", 841), ("A2", 594), ("A3", 420), ("A4", 297)):
            if long_edge >= edge - 5:
                return label
        return "A4"

    def _title_block_drawing(self) -> dict:
        bx1, by1, bx2, by2 = self._banner_rect_mm()
        bw = bx2 - bx1
        bh = by2 - by1
        sf = clamp(bh / 48.0, 0.8, 1.5)
        title_pt = 14.0 * sf
        dwg_pt = 10.0 * sf
        value_pt = 8.5 * sf
        label_pt = 6.0 * sf
        mini_pt = 6.6 * sf

        def fx(frac: float) -> float:
            return bx1 + bw * frac

        def fy(frac: float) -> float:
            return by1 + bh * frac

        lines: List[Tuple[float, float, float, float]] = [
            (bx1, by1, bx2, by1),
            (bx1, by2, bx2, by2),
            (bx1, by1, bx1, by2),
            (bx2, by1, bx2, by2),
            (bx1, fy(0.40), bx2, fy(0.40)),
            (bx1, fy(0.70), bx2, fy(0.70)),
            (fx(0.70), by1, fx(0.70), by2),
            (fx(0.35), fy(0.40), fx(0.35), by2),
        ]

        texts: List[dict] = []

        def add(frac_x: float, frac_y: float, text: str, pt: float, bold: bool = False):
            texts.append({"x": fx(frac_x) + 1.2, "y": fy(frac_y), "text": text, "pt": pt, "bold": bold, "anchor": "nw"})

        rev = self.rev_var.get().strip() or "-"
        size_label = self._paper_size_label()
        sheet = self.sheet_var.get().strip() or "-"
        scale = self.scale_text_var.get().strip() or "NTS"
        unit = self.unit_var.get().strip() or "mm"
        tol = f"{self.tol_x_var.get().strip()} / {self.tol_xx_var.get().strip()} / {self.tol_xxx_var.get().strip()}".strip(" /")

        add(0.012, 0.05, "TITLE", label_pt)
        add(0.012, 0.15, self.project_name_var.get().strip() or "Kabelboom", title_pt, bold=True)
        add(0.712, 0.05, "DWG NO", label_pt)
        add(0.712, 0.135, self.drawing_number_var.get().strip() or "-", dwg_pt, bold=True)
        add(0.712, 0.30, f"REV {rev}    SIZE {size_label}    SHEET {sheet}", mini_pt)

        add(0.012, 0.43, "DRAWN BY", label_pt)
        add(0.012, 0.55, self.engineer_var.get().strip() or "-", value_pt)
        add(0.362, 0.43, "DATE", label_pt)
        add(0.362, 0.55, self.date_drawn_var.get().strip() or "-", value_pt)
        add(0.712, 0.43, "CUSTOMER", label_pt)
        add(0.712, 0.55, self.customer_var.get().strip() or "-", value_pt)

        checked = f"{self.checked_by_var.get().strip() or '-'}   {self.date_checked_var.get().strip()}".strip()
        approved = f"{self.approved_by_var.get().strip() or '-'}   {self.date_approved_var.get().strip()}".strip()
        add(0.012, 0.73, "CHECKED BY", label_pt)
        add(0.012, 0.85, checked, value_pt)
        add(0.362, 0.73, "APPROVED BY", label_pt)
        add(0.362, 0.85, approved, value_pt)
        add(0.712, 0.72, f"SCALE {scale}", mini_pt)
        add(0.712, 0.86, f"UNIT {unit}    TOL {tol}", mini_pt)

        return {
            "rect": (bx1, by1, bx2, by2),
            "lines": lines,
            "texts": texts,
            "line_color": "#25364a",
            "text_color": "#162434",
        }

    def _zone_marker_drawing(self) -> dict:
        f = self.frame_margin_mm
        fx1, fy1, fx2, fy2 = f, f, self.paper_w_mm - f, self.paper_h_mm - f
        inner_w = fx2 - fx1
        inner_h = fy2 - fy1
        cols = max(4, int(round(inner_w / 52.0)))
        rows = max(3, min(26, int(round(inner_h / 52.0))))
        colw = inner_w / cols
        rowh = inner_h / rows

        lines: List[Tuple[float, float, float, float]] = []
        texts: List[dict] = []
        zone_pt = clamp(f * 0.62, 5.0, 9.0)
        letters = "ABCDEFGHIJKLMNOPQRSTUVWXYZ"

        for i in range(1, cols):
            x = fx1 + i * colw
            lines.append((x, 0.0, x, fy1))
            lines.append((x, fy2, x, self.paper_h_mm))
        for k in range(cols):
            cx = fx1 + (k + 0.5) * colw
            num = str(cols - k)
            texts.append({"x": cx, "y": fy1 * 0.5, "text": num, "pt": zone_pt, "bold": False, "anchor": "mm"})
            texts.append({"x": cx, "y": (fy2 + self.paper_h_mm) * 0.5, "text": num, "pt": zone_pt, "bold": False, "anchor": "mm"})

        for j in range(1, rows):
            y = fy1 + j * rowh
            lines.append((0.0, y, fx1, y))
            lines.append((fx2, y, self.paper_w_mm, y))
        for k in range(rows):
            cy = fy1 + (k + 0.5) * rowh
            letter = letters[rows - 1 - k] if rows - 1 - k < len(letters) else "?"
            texts.append({"x": fx1 * 0.5, "y": cy, "text": letter, "pt": zone_pt, "bold": False, "anchor": "mm"})
            texts.append({"x": (fx2 + self.paper_w_mm) * 0.5, "y": cy, "text": letter, "pt": zone_pt, "bold": False, "anchor": "mm"})

        return {"lines": lines, "texts": texts, "color": "#25364a"}

    def _pil_font(self, size_px: int, bold: bool = False):
        if not PIL_AVAILABLE or ImageFont is None:
            return None
        key = (max(6, int(round(size_px))), bool(bold))
        cached = self._pil_font_cache.get(key)
        if cached is not None:
            return cached

        font = None
        candidates = [
            "C:/Windows/Fonts/segoeuib.ttf" if bold else "C:/Windows/Fonts/segoeui.ttf",
            "C:/Windows/Fonts/arialbd.ttf" if bold else "C:/Windows/Fonts/arial.ttf",
            "DejaVuSans-Bold.ttf" if bold else "DejaVuSans.ttf",
        ]
        for candidate in candidates:
            try:
                font = ImageFont.truetype(candidate, key[0])
                break
            except Exception:
                continue
        if font is None:
            font = ImageFont.load_default()
        self._pil_font_cache[key] = font
        return font

    def _render_page_image(self, dpi: int = 180, show_grid: bool = True):
        if not PIL_AVAILABLE or Image is None or ImageDraw is None:
            raise RuntimeError("PNG/PDF export vereist Pillow op deze machine.")

        dpi = max(96, int(dpi))
        px_per_mm = dpi / 25.4
        width_px = max(32, int(round(self.paper_w_mm * px_per_mm)))
        height_px = max(32, int(round(self.paper_h_mm * px_per_mm)))
        image = Image.new("RGBA", (width_px, height_px), "#ffffff")
        draw = ImageDraw.Draw(image)

        def mm_to_px_x(value_mm: float) -> float:
            return value_mm * px_per_mm

        def mm_to_px_y(value_mm: float) -> float:
            return value_mm * px_per_mm

        def mm_point_to_px(point: Tuple[float, float]) -> Tuple[float, float]:
            return (mm_to_px_x(point[0]), mm_to_px_y(point[1]))

        def mm_width_to_px(width_mm: float, scale: float = 1.0, minimum: int = 1) -> int:
            return max(minimum, int(round(width_mm * scale * px_per_mm)))

        def draw_polyline(points: List[Tuple[float, float]], color: str, width_px_line: int):
            if len(points) < 2:
                return
            px_points = [mm_point_to_px(point) for point in points]
            try:
                draw.line(px_points, fill=color, width=width_px_line, joint="curve")
            except TypeError:
                draw.line(px_points, fill=color, width=width_px_line)

        def text_bbox(text: str, font, spacing_px: int = 0) -> Tuple[float, float]:
            if hasattr(draw, "multiline_textbbox"):
                box = draw.multiline_textbbox((0, 0), text or " ", font=font, spacing=spacing_px)
                return (box[2] - box[0], box[3] - box[1])
            sample = (text or " ").split("\n")
            width = 0.0
            line_h = font.size if hasattr(font, "size") else 12
            for line in sample:
                box = draw.textbbox((0, 0), line or " ", font=font) if hasattr(draw, "textbbox") else (0, 0, len(line) * line_h * 0.6, line_h)
                width = max(width, box[2] - box[0])
            height = max(1, len(sample)) * line_h + max(0, len(sample) - 1) * spacing_px
            return (width, height)

        def draw_anchored_text(x_px: float, y_px: float, text: str, fill: str, font, anchor: str = "nw", spacing_px: int = 2):
            content = text or ""
            width, height = text_bbox(content, font, spacing_px)
            left = x_px
            top = y_px
            if "e" in anchor:
                left -= width
            elif "w" not in anchor:
                left -= width / 2.0
            if "s" in anchor:
                top -= height
            elif "n" not in anchor:
                top -= height / 2.0
            draw.multiline_text((left, top), content, fill=fill, font=font, spacing=spacing_px)

        outer_w = mm_width_to_px(0.8, minimum=2)
        frame_w = mm_width_to_px(0.8, minimum=2)
        banner_w = mm_width_to_px(0.7, minimum=1)
        grid_color = "#edf2f7"

        draw.rectangle([(0, 0), (width_px - 1, height_px - 1)], fill="#ffffff", outline="#7c8aa0", width=outer_w)

        f = self.frame_margin_mm
        fx1 = mm_to_px_x(f)
        fy1 = mm_to_px_y(f)
        fx2 = mm_to_px_x(self.paper_w_mm - f)
        fy2 = mm_to_px_y(self.paper_h_mm - f)
        draw.rectangle([(fx1, fy1), (fx2, fy2)], outline="#25364a", width=frame_w)

        zone = self._zone_marker_drawing()
        for zx1, zy1, zx2, zy2 in zone["lines"]:
            draw.line([mm_point_to_px((zx1, zy1)), mm_point_to_px((zx2, zy2))], fill=zone["color"], width=max(1, int(round(frame_w * 0.6))))
        zone_font = self._pil_font(zone["texts"][0]["pt"] * dpi / 72.0, bold=False) if zone["texts"] else None
        for t in zone["texts"]:
            draw_anchored_text(mm_to_px_x(t["x"]), mm_to_px_y(t["y"]), t["text"], zone["color"], zone_font, anchor="mm")

        if show_grid:
            grid_mm = self._grid_step_mm() if self.snap_grid_enabled_var.get() else 10.0
            grid_mm = max(1.0, grid_mm)
            x = f
            while x <= self.paper_w_mm - f + 1e-6:
                px = mm_to_px_x(x)
                draw.line([(px, fy1), (px, fy2)], fill=grid_color, width=1)
                x += grid_mm
            y = f
            while y <= self.paper_h_mm - f + 1e-6:
                py = mm_to_px_y(y)
                draw.line([(fx1, py), (fx2, py)], fill=grid_color, width=1)
                y += grid_mm

        title_block = self._title_block_drawing()
        tb_x1, tb_y1, tb_x2, tb_y2 = title_block["rect"]
        draw.rectangle([mm_point_to_px((tb_x1, tb_y1)), mm_point_to_px((tb_x2, tb_y2))], fill="#ffffff")
        for lx1, ly1, lx2, ly2 in title_block["lines"]:
            draw.line([mm_point_to_px((lx1, ly1)), mm_point_to_px((lx2, ly2))], fill=title_block["line_color"], width=banner_w)
        for t in title_block["texts"]:
            font = self._pil_font(t["pt"] * dpi / 72.0, bold=t["bold"])
            draw_anchored_text(mm_to_px_x(t["x"]), mm_to_px_y(t["y"]), t["text"], title_block["text_color"], font, anchor=t["anchor"], spacing_px=2)

        font_connector = self._pil_font(9 * dpi / 72.0, bold=True)
        font_wire = self._pil_font(8 * dpi / 72.0)
        font_note = self._pil_font(10 * dpi / 72.0)
        font_table = self._pil_font(8 * dpi / 72.0)

        for note in self.image_notes:
            if self._drag_render_skip("image", note.image_id):
                continue
            image_bytes = self._image_note_bytes(note)
            if not image_bytes:
                continue
            width_mm, height_mm = self._image_note_display_size(note)
            target_w = max(1, int(round(width_mm * px_per_mm)))
            target_h = max(1, int(round(height_mm * px_per_mm)))
            try:
                with Image.open(io.BytesIO(image_bytes)) as img:
                    resampling = getattr(getattr(Image, "Resampling", Image), "LANCZOS", Image.LANCZOS)
                    rendered = img.convert("RGBA").resize((target_w, target_h), resampling)
                    image.paste(rendered, (int(round(mm_to_px_x(note.x_mm))), int(round(mm_to_px_y(note.y_mm)))), rendered)
            except Exception:
                continue

        for c in self.connectors:
            if self._drag_render_skip("connector", c.connector_id):
                continue
            world_polylines, bbox = self._connector_world_geometry(c)
            stroke_w = mm_width_to_px(c.line_width_mm, scale=0.4)
            for poly in world_polylines:
                draw_polyline(poly, c.line_color, stroke_w)
            draw_anchored_text(mm_to_px_x(c.x_mm + c.label_dx_mm), mm_to_px_y(c.y_mm + c.label_dy_mm), c.connector_id, "#0d2238", font_connector, anchor="center")

        bridge_specs = self._wire_bridge_specs()
        for w in self.wires:
            if self._drag_render_skip("wire", w.wire_id):
                continue
            stroke_w = mm_width_to_px(w.width_mm, scale=0.4)
            wire_bridges = bridge_specs.get(w.wire_id, [])
            if wire_bridges and self._wire_supports_bridge(w):
                centerline = self._wire_centerline_points(w, curve_samples=60)
                segments = self._polyline_without_ranges(centerline, [(spec["clear_start"], spec["clear_end"]) for spec in wire_bridges])
                for segment in segments:
                    draw_polyline(segment, w.color, stroke_w)
                total_length = self._polyline_length(centerline)
                for spec in wire_bridges:
                    start_point, _ = self._point_and_tangent_on_polyline(centerline, clamp(spec["arc_start"], 0.0, total_length))
                    end_point, _ = self._point_and_tangent_on_polyline(centerline, clamp(spec["arc_end"], 0.0, total_length))
                    bridge_points = self._bridge_curve_points(start_point, end_point, spec["normal"], spec["height"], samples=24)
                    draw_polyline(bridge_points, w.color, stroke_w)
            else:
                for idx, poly in enumerate(self._wire_display_polylines(w)):
                    draw_polyline(poly, w.color if idx == 0 else w.color_b, stroke_w)
            if w.label:
                mx, my = self._wire_label_position(w)
                draw_anchored_text(mm_to_px_x(mx + 1.2), mm_to_px_y(my - 1.2), w.label, "#1f2937", font_wire, anchor="sw")

        for l in self.leaders:
            if self._drag_render_skip("leader", l.leader_id):
                continue
            stroke_w = mm_width_to_px(l.width_mm, scale=0.4)
            draw_polyline(self._leader_polyline(l), l.color, stroke_w)
            draw.polygon([mm_point_to_px(point) for point in self._leader_arrow_points(l)], fill=l.color, outline=l.color)
            label_bbox = self._leader_text_bbox(l)
            if label_bbox:
                bx1, by1, bx2, by2 = label_bbox
                if l.text_box:
                    draw.rectangle(
                        [(mm_to_px_x(bx1), mm_to_px_y(by1)), (mm_to_px_x(bx2), mm_to_px_y(by2))],
                        fill="white",
                        outline=l.color,
                        width=1,
                    )
                leader_font = self._pil_font(l.text_size_pt * dpi / 72.0)
                draw_anchored_text(mm_to_px_x(bx1 + 1.2), mm_to_px_y(by1 + 0.9), l.text, l.color, leader_font, anchor="nw")

        for dim in self.dimensions:
            if self._drag_render_skip("dimension", dim.dim_id):
                continue
            geo = self._dimension_geometry(dim)
            dim_stroke = mm_width_to_px(dim.line_width_mm, scale=0.4)
            draw_polyline([geo["feet"][0], geo["feet"][1]], dim.color, dim_stroke)
            for seg_start, seg_end in geo["ext"]:
                draw_polyline([seg_start, seg_end], dim.color, max(1, int(round(dim_stroke * 0.8))))
            for tri in geo["arrows"]:
                draw.polygon([mm_point_to_px(point) for point in tri], fill=dim.color, outline=dim.color)
            dim_font = self._pil_font(dim.text_size_pt * dpi / 72.0)
            draw_anchored_text(mm_to_px_x(geo["text_pos"][0]), mm_to_px_y(geo["text_pos"][1]), geo["text"], dim.color, dim_font, anchor="mm")

        for t in self.tables:
            if self._drag_render_skip("table", t.table_id):
                continue
            widths = self._table_col_widths(t)
            heights = self._table_row_heights(t)
            tw = sum(widths)
            th = sum(heights)
            stroke_w = mm_width_to_px(t.border_width_mm, scale=0.4)
            draw.rectangle(
                [(mm_to_px_x(t.x_mm), mm_to_px_y(t.y_mm)), (mm_to_px_x(t.x_mm + tw), mm_to_px_y(t.y_mm + th))],
                outline=t.border_color,
                width=stroke_w,
            )
            x = t.x_mm
            for col in range(1, t.cols):
                x += widths[col - 1]
                px = mm_to_px_x(x)
                draw.line([(px, mm_to_px_y(t.y_mm)), (px, mm_to_px_y(t.y_mm + th))], fill=t.border_color, width=max(1, stroke_w))
            y = t.y_mm
            for row in range(1, t.rows):
                y += heights[row - 1]
                py = mm_to_px_y(y)
                draw.line([(mm_to_px_x(t.x_mm), py), (mm_to_px_x(t.x_mm + tw), py)], fill=t.border_color, width=max(1, stroke_w))
            for r in range(t.rows):
                for c in range(t.cols):
                    txt = t.cells[r][c] if r < len(t.cells) and c < len(t.cells[r]) else ""
                    if not txt:
                        continue
                    cell_x, cell_y, cw, ch = self._table_cell_rect(t, r, c)
                    tx, ty, anchor = self._table_text_anchor_position(t, cell_x, cell_y, cw, ch)
                    draw_anchored_text(mm_to_px_x(tx), mm_to_px_y(ty), txt, "#1f2937", font_table, anchor=anchor, spacing_px=2)

        for note in self.text_notes:
            if self._drag_render_skip("text", note.note_id):
                continue
            draw_anchored_text(mm_to_px_x(note.x_mm), mm_to_px_y(note.y_mm), note.text, note.color, self._pil_font(note.font_size_pt * dpi / 72.0), anchor="nw", spacing_px=max(2, int(round(dpi / 90))))

        return image

    def export_png(self):
        name = safe_name(self.project_name_var.get(), "kabelboom")
        path = self._ask_save_filename(
            title="Exporteer PNG",
            defaultextension=".png",
            initialfile=f"{name}_tekening.png",
            filetypes=[("PNG", "*.png"), ("Alle bestanden", "*.*")],
            settings_key="last_export_dir",
        )
        if not path:
            return
        try:
            image = self._render_page_image(dpi=180)
            image.save(path, format="PNG", dpi=(180, 180))
            self.status(f"PNG geexporteerd: {Path(path).name}")
            self._show_info(f"PNG gemaakt:\n{path}")
        except Exception as exc:
            self._show_error(f"PNG export mislukt:\n{exc}")

    def export_pdf(self):
        name = safe_name(self.project_name_var.get(), "kabelboom")
        path = self._ask_save_filename(
            title="Exporteer PDF",
            defaultextension=".pdf",
            initialfile=f"{name}_tekening.pdf",
            filetypes=[("PDF", "*.pdf"), ("Alle bestanden", "*.*")],
            settings_key="last_export_dir",
        )
        if not path:
            return
        try:
            image = self._render_page_image(dpi=220)
            image.convert("RGB").save(path, format="PDF", resolution=220.0)
            self.status(f"PDF geexporteerd: {Path(path).name}")
            self._show_info(f"PDF gemaakt:\n{path}")
        except Exception as exc:
            self._show_error(f"PDF export mislukt:\n{exc}")

    # ---------------- Status / Mode ----------------
    def status(self, text: str):
        if self.status_var.get() != text:
            self.status_var.set(text)

    def _prepare_dialog_parent(self):
        try:
            self.update_idletasks()
            self.deiconify()
            self.lift()
            self.focus_force()
        except Exception:
            pass

    def _dialog_kwargs(self, **kwargs):
        self._prepare_dialog_parent()
        out = {"parent": self}
        out.update(kwargs)
        return out

    SHORTCUT_GROUPS = [
        ("Algemeen", [
            ("Ctrl+N", "Nieuw project"),
            ("Ctrl+O", "Project openen"),
            ("Ctrl+S", "Opslaan"),
            ("Ctrl+Shift+S", "Opslaan als"),
            ("Ctrl+Z / Ctrl+Y", "Undo / Redo"),
            ("F1", "Dit overzicht"),
        ]),
        ("Bewerken", [
            ("Ctrl+V", "Plak tekst of afbeelding uit klembord"),
            ("Ctrl+D", "Dupliceer selectie"),
            ("Delete", "Verwijder selectie"),
            ("Esc", "Terug naar Selecteer/verplaats; stopt huidige actie"),
            ("Enter", "Stop de draadketen"),
            ("Shift / Ctrl + klik", "Selectie uitbreiden"),
        ]),
        ("Muis", [
            ("Muiswiel", "In-/uitzoomen"),
            ("Middenmuis slepen", "Pannen"),
            ("Rechtermuisknop", "Contextopties (extra bewerkacties)"),
            ("Shift bij draad", "Recht tekenen (horizontaal/verticaal)"),
            ("Dubbelklik bibliotheek", "Connector direct plaatsen"),
        ]),
        ("Snappen", [
            ("Grid snap", "Vangt op het rasterpunt (stap instelbaar)"),
            ("Endpoint snap", "Vangt op draadeinden om door te tekenen"),
        ]),
    ]

    def new_project_from_template(self):
        """Mini-startscherm: kies een bladformaat-sjabloon en begin een nieuw project."""
        self._prepare_dialog_parent()
        win = tk.Toplevel(self)
        win.title("Nieuw uit sjabloon")
        win.transient(self)
        win.resizable(False, False)
        result = {"preset": None}

        container = ttk.Frame(win, padding=16)
        container.grid(row=0, column=0, sticky="nsew")
        ttk.Label(
            container,
            text="Kies een bladformaat om mee te beginnen",
            font=(getattr(self, "_font_family_heading", "Sitka Heading"), 14),
            foreground=ui_theme.color(self._app_theme, "header_fg"),
        ).grid(row=0, column=0, sticky="w", pady=(0, 12))
        presets = [p for p in PAPER_PRESET_OPTIONS if p != PAPER_PRESET_CUSTOM]
        choice_var = tk.StringVar(value=DEFAULT_PAPER_PRESET if DEFAULT_PAPER_PRESET in presets else (presets[0] if presets else ""))
        radios = ttk.Frame(container)
        radios.grid(row=1, column=0, sticky="w")
        for idx, preset in enumerate(presets):
            ttk.Radiobutton(radios, text=preset, value=preset, variable=choice_var).grid(
                row=idx // 2, column=idx % 2, sticky="w", padx=(0, 18), pady=1
            )

        def _create():
            result["preset"] = choice_var.get()
            win.destroy()

        def _cancel():
            win.destroy()

        win.protocol("WM_DELETE_WINDOW", _cancel)
        win.bind("<Escape>", lambda _e: _cancel())
        win.bind("<Return>", lambda _e: _create())
        buttons = ttk.Frame(container)
        buttons.grid(row=2, column=0, sticky="e", pady=(14, 0))
        ttk.Button(buttons, text="Annuleren", command=_cancel).grid(row=0, column=0, padx=(0, 6))
        ttk.Button(buttons, text="Maak project", style="Primary.TButton", command=_create).grid(row=0, column=1)

        win.update_idletasks()
        try:
            px = self.winfo_rootx() + max(0, (self.winfo_width() - win.winfo_width()) // 2)
            py = self.winfo_rooty() + max(0, (self.winfo_height() - win.winfo_height()) // 3)
            win.geometry(f"+{px}+{py}")
        except tk.TclError:
            pass
        win.grab_set()
        win.focus_set()
        self.wait_window(win)

        if result["preset"]:
            self.new_project(paper_preset=result["preset"])
            self.fit_page_to_view()
            self.status(f"Nieuw project ({result['preset']}).")

    def show_shortcuts_dialog(self):
        self._prepare_dialog_parent()
        if getattr(self, "_shortcuts_window", None) is not None:
            try:
                self._shortcuts_window.deiconify()
                self._shortcuts_window.lift()
                self._shortcuts_window.focus_set()
                return
            except tk.TclError:
                self._shortcuts_window = None
        win = tk.Toplevel(self)
        win.title("Sneltoetsen & muisbediening")
        win.transient(self)
        win.resizable(False, False)
        try:
            win.configure(background=ui_theme.color(self._app_theme, "app_bg"))
        except Exception:
            pass
        self._shortcuts_window = win

        def _close():
            self._shortcuts_window = None
            win.destroy()

        win.protocol("WM_DELETE_WINDOW", _close)
        win.bind("<Escape>", lambda _e: _close())

        container = ttk.Frame(win, padding=14)
        container.grid(row=0, column=0, sticky="nsew")
        ttk.Label(
            container,
            text="Sneltoetsen & muisbediening",
            font=(getattr(self, "_font_family_heading", "Sitka Heading"), 15),
            foreground=ui_theme.color(self._app_theme, "header_fg"),
        ).grid(row=0, column=0, columnspan=2, sticky="w", pady=(0, 10))
        row = 1
        for group_title, entries in self.SHORTCUT_GROUPS:
            frame = ttk.LabelFrame(container, text=group_title, padding=(10, 6))
            frame.grid(row=row, column=0, sticky="ew", pady=(0, 8))
            frame.columnconfigure(1, weight=1)
            for idx, (keys, desc) in enumerate(entries):
                ttk.Label(frame, text=keys, style="Subtle.TLabel", width=20, anchor="w").grid(row=idx, column=0, sticky="w", padx=(0, 10))
                ttk.Label(frame, text=desc, anchor="w").grid(row=idx, column=1, sticky="w")
            row += 1
        ttk.Button(container, text="Sluiten", command=_close).grid(row=row, column=0, sticky="e", pady=(2, 0))
        win.update_idletasks()
        try:
            px = self.winfo_rootx() + max(0, (self.winfo_width() - win.winfo_width()) // 2)
            py = self.winfo_rooty() + max(0, (self.winfo_height() - win.winfo_height()) // 3)
            win.geometry(f"+{px}+{py}")
        except tk.TclError:
            pass
        win.focus_set()

    def _show_info(self, message: str, title: str = APP_TITLE):
        self._prepare_dialog_parent()
        return messagebox.showinfo(title, message, parent=self)

    def _show_warning(self, message: str, title: str = APP_TITLE):
        self._prepare_dialog_parent()
        return messagebox.showwarning(title, message, parent=self)

    def _show_error(self, message: str, title: str = APP_TITLE):
        self._prepare_dialog_parent()
        return messagebox.showerror(title, message, parent=self)

    def _ask_open_filename(self, **kwargs):
        settings_key = kwargs.pop("settings_key", None)
        if settings_key and "initialdir" not in kwargs:
            initialdir = self._initial_dir(settings_key)
            if initialdir:
                kwargs["initialdir"] = initialdir
        return filedialog.askopenfilename(**self._dialog_kwargs(**kwargs))

    def _ask_save_filename(self, **kwargs):
        settings_key = kwargs.pop("settings_key", None)
        if settings_key and "initialdir" not in kwargs:
            initialdir = self._initial_dir(settings_key)
            if initialdir:
                kwargs["initialdir"] = initialdir
        path = filedialog.asksaveasfilename(**self._dialog_kwargs(**kwargs))
        if path and settings_key:
            self._remember_dialog_path(settings_key, path)
        return path

    def _save_settings(self, **values):
        self.settings = update_app_settings(APP_SETTINGS_KEY, **values)

    def _initial_dir(self, key: str) -> str | None:
        return existing_dir(self.settings.get(key))

    def _remember_dialog_path(self, key: str, path_text: str):
        self._save_settings(**{key: parent_dir(path_text)})

    def _ask_string(self, prompt: str, initialvalue: str = ""):
        self._prepare_dialog_parent()
        return simpledialog.askstring(APP_TITLE, prompt, parent=self, initialvalue=initialvalue)

    def _ask_integer(self, prompt: str, initialvalue: int, minvalue: Optional[int] = None, maxvalue: Optional[int] = None):
        self._prepare_dialog_parent()
        return simpledialog.askinteger(APP_TITLE, prompt, parent=self, initialvalue=initialvalue, minvalue=minvalue, maxvalue=maxvalue)

    def _ask_float(self, prompt: str, initialvalue: float, minvalue: Optional[float] = None, maxvalue: Optional[float] = None):
        self._prepare_dialog_parent()
        return simpledialog.askfloat(APP_TITLE, prompt, parent=self, initialvalue=initialvalue, minvalue=minvalue, maxvalue=maxvalue)

    def request_redraw(self):
        if self._redraw_scheduled:
            return
        self._redraw_scheduled = True
        self._redraw_after_id = self.after(self._redraw_interval_ms, self._flush_redraw)

    def _invalidate_aa_scene(self):
        self._aa_scene_dirty = True

    def request_scene_redraw(self):
        """Plan een redraw nadat model- of titelblokinhoud is gewijzigd."""

        self._invalidate_aa_scene()
        self.request_redraw()

    def _flush_redraw(self):
        self._redraw_scheduled = False
        self._redraw_after_id = None
        self.redraw()

    def _cancel_pending_redraw(self):
        if self._redraw_after_id is not None:
            try:
                self.after_cancel(self._redraw_after_id)
            except Exception:
                pass
            self._redraw_after_id = None
        self._redraw_scheduled = False

    def _toggle_perf_meter(self):
        self._show_perf_meter = bool(self.perf_meter_var.get())
        if self._show_perf_meter:
            self.status("Prestatiemeter aan: redraw-tijd verschijnt rechtsonder in de statusbalk.")
            self.redraw()
        else:
            self.perf_var.set("")
            self.status("Prestatiemeter uit.")

    def stress_fill_wires(self, count: Optional[int] = None):
        """Debug-hulp: vul het blad met N testdraden om de redraw-tijd te meten.

        Bereikbaar via Tools en gebruikt door tools/bench_redraw.py. De draden zijn
        gewone WirePath-objecten met lengte + doorsnede, dus ze tellen ook mee in
        netlist/BOM en zijn met Undo in één keer terug te draaien.
        """
        if count is None:
            count = self._ask_integer(
                "Aantal testdraden om toe te voegen (meet de redraw-prestaties):",
                initialvalue=200, minvalue=1, maxvalue=5000,
            )
        if not count:
            return
        before = self._capture_before_change()
        margin = self.frame_margin_mm + 6.0
        x0, x1 = margin, max(margin + 10.0, self.paper_w_mm - margin)
        y0, y1 = margin, max(margin + 10.0, self.paper_h_mm - margin)
        rnd = random.Random(1234)
        colors = ["#1f4e79", "#b03a2e", "#1e8449", "#7d3c98", "#b9770e", "#34495e"]
        sections = [0.22, 0.35, 0.5, 0.75, 1.0, 1.5, 2.5]
        styles = ["straight", "straight", "straight", "curve", "twisted_pair"]
        existing = [w.wire_id for w in self.wires]
        for _ in range(int(count)):
            cx, cy = rnd.uniform(x0, x1), rnd.uniform(y0, y1)
            pts = [(cx, cy)]
            for _seg in range(rnd.randint(1, 3)):
                cx = clamp(cx + rnd.uniform(-60.0, 60.0), x0, x1)
                cy = clamp(cy + rnd.uniform(-40.0, 40.0), y0, y1)
                pts.append((cx, cy))
            wire_id = self._next_id("W", existing)
            existing.append(wire_id)
            length = sum(math.dist(pts[i], pts[i + 1]) for i in range(len(pts) - 1))
            self.wires.append(WirePath(
                wire_id=wire_id,
                points_mm=pts,
                color=rnd.choice(colors),
                width_mm=1.2,
                cross_section_mm2=rnd.choice(sections),
                length_mm=length,
                style=rnd.choice(styles),
            ))
        self._commit_change(before, f"{int(count)} testdraden toegevoegd (totaal {len(self.wires)} draden).")
        self.redraw()

    def _on_shortcut_save(self, _event=None):
        self.save_project()
        return "break"

    def _on_shortcut_save_as(self, _event=None):
        self.save_project_as()
        return "break"

    def _on_shortcut_open(self, _event=None):
        self.open_project()
        return "break"

    def _on_shortcut_new(self, _event=None):
        self.new_project()
        return "break"

    def _on_shortcut_delete(self, _event=None):
        self.delete_selected()
        return "break"

    def _on_shortcut_duplicate(self, _event=None):
        self.duplicate_selected()
        return "break"

    def _reset_history(self):
        self._history_undo = []
        self._history_redo = []
        self._drag_history_before = None
        self._drag_history_changed = False

    def _snapshot_project(self) -> str:
        return json.dumps(self._project_dict(), ensure_ascii=False, sort_keys=True)

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
            parent=self,
        )
        if answer is None:
            return False
        if answer:
            return bool(self.save_project())
        return True

    def _on_close(self):
        if self._confirm_discard_changes():
            self.destroy()

    def _capture_before_change(self) -> Optional[str]:
        self._invalidate_aa_scene()
        if self._history_replaying:
            return None
        return self._snapshot_project()

    def _commit_change(self, before_snapshot: Optional[str], status_text: Optional[str] = None):
        self._clear_connector_caches()
        self._clear_wire_caches()
        if before_snapshot is None or self._history_replaying:
            if status_text:
                self.status(status_text)
            return
        after_snapshot = self._snapshot_project()
        if after_snapshot == before_snapshot:
            return
        self._history_undo.append(before_snapshot)
        if len(self._history_undo) > self._history_limit:
            self._history_undo = self._history_undo[-self._history_limit :]
        self._history_redo.clear()
        if status_text:
            self.status(status_text)

    def undo(self, _event=None):
        if not self._history_undo:
            self.status("Niets om ongedaan te maken.")
            return "break"
        current = self._snapshot_project()
        previous = self._history_undo.pop()
        self._history_redo.append(current)
        self._history_replaying = True
        try:
            self._load_project_dict(json.loads(previous))
        finally:
            self._history_replaying = False
        self.status("Undo uitgevoerd.")
        return "break"

    def redo(self, _event=None):
        if not self._history_redo:
            self.status("Niets om opnieuw uit te voeren.")
            return "break"
        current = self._snapshot_project()
        nxt = self._history_redo.pop()
        self._history_undo.append(current)
        self._history_replaying = True
        try:
            self._load_project_dict(json.loads(nxt))
        finally:
            self._history_replaying = False
        self.status("Redo uitgevoerd.")
        return "break"

    def _on_snap_settings_changed(self, _event=None):
        step = max(0.1, try_float(self.snap_grid_mm_var.get(), 1.0))
        self.snap_grid_mm_var.set(f"{step:g}")
        self.redraw()
        return "break"

    def _on_ui_scale_changed(self):
        percent = set_ui_scale(self, self.ui_scale_var.get(), apply_tk_scaling=False)
        self.ui_scale_var.set(f"{percent}%")
        self._save_settings(ui_scale_percent=percent)
        self._apply_control_ui_scale()
        self._apply_panel_layout(persist=False)
        self._update_left_panel_scrollregion()
        self.request_redraw()
        self.status(f"UI schaal: {percent}%")

    def _grid_step_mm(self) -> float:
        return max(0.1, try_float(self.snap_grid_mm_var.get(), 1.0))

    def _sync_wire_bridge_vars(self):
        if not hasattr(self, "wire_bridge_enabled_var"):
            return
        self.wire_bridge_enabled_var.set(bool(self.wire_bridge_enabled))
        self.wire_bridge_height_var.set(f"{self.wire_bridge_height_mm:g}")
        self.wire_bridge_length_var.set(f"{self.wire_bridge_length_mm:g}")
        self.wire_bridge_clearance_var.set(f"{self.wire_bridge_clearance_mm:g}")

    def _wire_bridge_settings_signature(self) -> Tuple[bool, float, float, float]:
        length = max(0.5, float(getattr(self, "wire_bridge_length_mm", DEFAULT_WIRE_BRIDGE_LENGTH_MM)))
        clearance = max(length, float(getattr(self, "wire_bridge_clearance_mm", DEFAULT_WIRE_BRIDGE_CLEARANCE_MM)))
        return (
            bool(getattr(self, "wire_bridge_enabled", DEFAULT_WIRE_BRIDGE_ENABLED)),
            round(max(0.1, float(getattr(self, "wire_bridge_height_mm", DEFAULT_WIRE_BRIDGE_HEIGHT_MM))), 4),
            round(length, 4),
            round(clearance, 4),
        )

    def apply_wire_bridge_settings(self):
        before = self._capture_before_change()
        enabled = bool(self.wire_bridge_enabled_var.get())
        height = max(0.1, try_float(self.wire_bridge_height_var.get(), self.wire_bridge_height_mm))
        length = max(0.5, try_float(self.wire_bridge_length_var.get(), self.wire_bridge_length_mm))
        clearance = max(length, try_float(self.wire_bridge_clearance_var.get(), self.wire_bridge_clearance_mm))
        self.wire_bridge_enabled = enabled
        self.wire_bridge_height_mm = height
        self.wire_bridge_length_mm = length
        self.wire_bridge_clearance_mm = clearance
        self._sync_wire_bridge_vars()
        self._clear_wire_geometry_caches()
        self.request_redraw()
        state = "aan" if enabled else "uit"
        self._commit_change(before, f"Kruising-boogjes {state}; hoogte {height:g} mm, lengte {length:g} mm.")

    def reset_wire_bridge_settings(self):
        self.wire_bridge_enabled_var.set(DEFAULT_WIRE_BRIDGE_ENABLED)
        self.wire_bridge_height_var.set(f"{DEFAULT_WIRE_BRIDGE_HEIGHT_MM:g}")
        self.wire_bridge_length_var.set(f"{DEFAULT_WIRE_BRIDGE_LENGTH_MM:g}")
        self.wire_bridge_clearance_var.set(f"{DEFAULT_WIRE_BRIDGE_CLEARANCE_MM:g}")
        self.apply_wire_bridge_settings()

    def _snap_to_grid(self, point: Tuple[float, float]) -> Tuple[float, float]:
        step = self._grid_step_mm()
        return (round(point[0] / step) * step, round(point[1] / step) * step)

    def _snap_candidate_signature_value(self) -> Tuple:
        return (
            tuple((round(c.x_mm, 4), round(c.y_mm, 4)) for c in self.connectors),
            tuple(
                (
                    w.wire_id,
                    round(w.points_mm[0][0], 4),
                    round(w.points_mm[0][1], 4),
                    round(w.points_mm[-1][0], 4),
                    round(w.points_mm[-1][1], 4),
                )
                for w in self.wires
                if len(w.points_mm) >= 2
            ),
            tuple((round(l.start_mm[0], 4), round(l.start_mm[1], 4), round(l.end_mm[0], 4), round(l.end_mm[1], 4)) for l in self.leaders),
            tuple(
                (
                    t.table_id,
                    round(t.x_mm, 4),
                    round(t.y_mm, 4),
                    round(self._table_size(t)[0], 4),
                    round(self._table_size(t)[1], 4),
                )
                for t in self.tables
            ),
        )

    def _base_snap_candidates(self) -> List[Tuple[float, float]]:
        signature = self._snap_candidate_signature_value()
        if signature == self._snap_candidate_signature:
            return self._snap_candidate_cache

        points: List[Tuple[float, float]] = []
        for c in self.connectors:
            points.append((c.x_mm, c.y_mm))
        for w in self.wires:
            if len(w.points_mm) >= 2:
                points.append(w.points_mm[0])
                points.append(w.points_mm[-1])
        for l in self.leaders:
            points.append(l.start_mm)
            points.append(l.end_mm)
        for t in self.tables:
            tw, th = self._table_size(t)
            points.extend(
                [
                    (t.x_mm, t.y_mm),
                    (t.x_mm + tw, t.y_mm),
                    (t.x_mm, t.y_mm + th),
                    (t.x_mm + tw, t.y_mm + th),
                ]
            )
        self._snap_candidate_signature = signature
        self._snap_candidate_cache = points
        return points

    def _snap_candidates(
        self,
        exclude_wire_endpoints: Optional[set[Tuple[str, int]]] = None,
        exclude_points: Optional[set[Tuple[float, float]]] = None,
    ) -> List[Tuple[float, float]]:
        exclude_wire_endpoints = exclude_wire_endpoints or set()
        exclude_points = exclude_points or set()
        points = list(self._base_snap_candidates())
        if exclude_wire_endpoints:
            excluded_points = {
                w.points_mm[0 if endpoint_index == 0 else -1]
                for w in self.wires
                for endpoint_index in (0, 1)
                if len(w.points_mm) >= 2 and (w.wire_id, endpoint_index) in exclude_wire_endpoints
            }
            if excluded_points:
                points = [point for point in points if point not in excluded_points]
        if exclude_points:
            points = [
                point
                for point in points
                if not any(math.dist(point, excluded_point) <= 1e-6 for excluded_point in exclude_points)
            ]
        if self.mode == "draw_wire" and self.temp_wire_points:
            points.append(self.temp_wire_points[-1])
        return points

    def _snap_to_endpoint(
        self,
        point: Tuple[float, float],
        exclude_wire_endpoints: Optional[set[Tuple[str, int]]] = None,
        exclude_points: Optional[set[Tuple[float, float]]] = None,
    ) -> Tuple[Tuple[float, float], Optional[Tuple[float, float]]]:
        candidates = self._snap_candidates(exclude_wire_endpoints=exclude_wire_endpoints, exclude_points=exclude_points)
        if not candidates:
            return (point, None)
        best = point
        snapped_candidate = None
        best_dist = float("inf")
        tol_mm = max(1.0, 6.0 / self.zoom)
        for cx, cy in candidates:
            dist = math.hypot(point[0] - cx, point[1] - cy)
            if dist < best_dist:
                best_dist = dist
                best = (cx, cy)
                snapped_candidate = (cx, cy)
        if best_dist <= tol_mm:
            return (best, snapped_candidate)
        return (point, None)

    def _snap_world(
        self,
        point: Tuple[float, float],
        event=None,
        exclude_wire_endpoints: Optional[set[Tuple[str, int]]] = None,
        exclude_points: Optional[set[Tuple[float, float]]] = None,
    ) -> Tuple[float, float]:
        snapped = point
        if self.snap_endpoint_enabled_var.get():
            snapped, _candidate = self._snap_to_endpoint(
                snapped,
                exclude_wire_endpoints=exclude_wire_endpoints,
                exclude_points=exclude_points,
            )
        if self.mode == "draw_wire" and self.temp_wire_points and event is not None and self._is_shift_pressed(event):
            snapped = self._orthogonal_snap(snapped, self.temp_wire_points[-1])
        if self.snap_grid_enabled_var.get():
            snapped = self._snap_to_grid(snapped)
        if self.snap_endpoint_enabled_var.get():
            snapped, _candidate = self._snap_to_endpoint(
                snapped,
                exclude_wire_endpoints=exclude_wire_endpoints,
                exclude_points=exclude_points,
            )
        return snapped

    def fit_page_to_view(self):
        cw = max(200, self.canvas.winfo_width() - 40)
        ch = max(200, self.canvas.winfo_height() - 40)
        zx = cw / max(1.0, self.paper_w_mm)
        zy = ch / max(1.0, self.paper_h_mm)
        self.zoom = clamp(min(zx, zy), 0.3, 8.0)
        self.pan_x = (self.canvas.winfo_width() - self.paper_w_mm * self.zoom) / 2.0
        self.pan_y = (self.canvas.winfo_height() - self.paper_h_mm * self.zoom) / 2.0
        self.redraw()
        self.status("Blad passend in beeld gezet.")

    def _sync_paper_preset_var(self):
        label = paper_preset_for_dimensions(self.paper_w_mm, self.paper_h_mm)
        if self.paper_preset_var.get() != label:
            self.paper_preset_var.set(label)

    def set_paper_preset(self, label: str, fit_view: bool = False, announce: bool = False, commit: bool = False):
        dims = paper_preset_dimensions(label)
        if dims is None:
            self._sync_paper_preset_var()
            return
        if abs(self.paper_w_mm - dims[0]) <= 1e-6 and abs(self.paper_h_mm - dims[1]) <= 1e-6:
            self._sync_paper_preset_var()
            return
        before = self._capture_before_change() if commit else None
        self.paper_w_mm = dims[0]
        self.paper_h_mm = dims[1]
        self._sync_paper_preset_var()
        if fit_view:
            self.fit_page_to_view()
        else:
            self.redraw()
        if announce:
            self.status(f"Bladformaat ingesteld op {label}.")
        if commit:
            self._commit_change(before, f"Bladformaat gewijzigd naar {label}.")

    def _on_paper_preset_changed(self, _event=None):
        label = self.paper_preset_var.get().strip()
        if label == PAPER_PRESET_CUSTOM:
            self._sync_paper_preset_var()
            self.status("Kies een IEC A-formaat om de tekeningrand te wijzigen.")
            return
        self.set_paper_preset(label, fit_view=True, announce=True, commit=True)

    def _sync_wire_defaults_from_property_panel(self) -> bool:
        if self.selected_items and any(kind != "wire" for kind, _ident in self.selected_items):
            return False
        if self.selected and self.selected[0] != "wire":
            return False
        self.default_wire_color = self.prop_color_var.get().strip() or self.default_wire_color
        self.default_wire_color_b = self.prop_color_b_var.get().strip() or self.default_wire_color_b
        self.default_wire_width_mm = max(0.2, try_float(self.prop_width_var.get(), self.default_wire_width_mm))
        self.default_wire_style = wire_style_internal(self.prop_wire_style_var.get().strip())
        self.default_wire_curve_offset_mm = try_float(self.prop_curve_var.get(), self.default_wire_curve_offset_mm)
        self.default_wire_twist_pitch_mm = max(1.0, try_float(self.prop_twist_pitch_var.get(), self.default_wire_twist_pitch_mm))
        self.default_wire_pair_gap_mm = max(0.2, try_float(self.prop_pair_gap_var.get(), self.default_wire_pair_gap_mm))
        return True

    def _sync_leader_defaults_from_property_panel(self) -> bool:
        if self.selected_items and any(kind != "leader" for kind, _ident in self.selected_items):
            return False
        if self.selected and self.selected[0] != "leader":
            return False
        self.default_leader_color = self.prop_color_var.get().strip() or self.default_leader_color
        self.default_leader_width_mm = max(0.2, try_float(self.prop_width_var.get(), self.default_leader_width_mm))
        self.default_leader_arrow_size_mm = max(0.5, try_float(self.prop_scale_var.get(), self.default_leader_arrow_size_mm))
        self.default_leader_text_size_pt = max(4.0, try_float(self.prop_leader_text_size_var.get(), self.default_leader_text_size_pt))
        self.default_leader_text_box = bool(self.prop_leader_text_box_var.get())
        return True

    def _sync_dimension_defaults_from_property_panel(self) -> bool:
        if self.selected_items and any(kind != "dimension" for kind, _ident in self.selected_items):
            return False
        if self.selected and self.selected[0] != "dimension":
            return False
        self.default_dimension_color = self.prop_color_var.get().strip() or self.default_dimension_color
        self.default_dimension_line_width_mm = max(0.1, try_float(self.prop_width_var.get(), self.default_dimension_line_width_mm))
        self.default_dimension_arrow_size_mm = max(0.5, try_float(self.prop_scale_var.get(), self.default_dimension_arrow_size_mm))
        self.default_dimension_text_size_pt = max(4.0, try_float(self.prop_leader_text_size_var.get(), self.default_dimension_text_size_pt))
        self.default_dimension_offset_mm = try_float(self.prop_dim_offset_var.get(), self.default_dimension_offset_mm)
        self.default_dimension_orientation = dimension_orientation_internal(self.prop_dim_orientation_var.get().strip())
        self.default_dimension_tolerance = self.prop_dim_tolerance_var.get().strip()
        return True

    def set_mode(self, mode: str):
        synced_wire_defaults = False
        synced_leader_defaults = False
        synced_dimension_defaults = False
        if mode == "draw_wire":
            synced_wire_defaults = self._sync_wire_defaults_from_property_panel()
        elif mode == "draw_leader":
            synced_leader_defaults = self._sync_leader_defaults_from_property_panel()
        elif mode == "draw_dimension":
            synced_dimension_defaults = self._sync_dimension_defaults_from_property_panel()
        self.mode = mode
        label = {
            "select": "Select",
            "place_connector": "Connector plaatsen",
            "draw_wire": "Draad tekenen",
            "draw_leader": "Leader tekenen",
            "draw_dimension": "Maatlijn tekenen",
            "draw_table": "Tabel plaatsen",
        }.get(mode, mode)
        self.mode_var.set(f"Mode: {label}")
        self._update_mode_buttons()
        self.cancel_temporary_action()
        if mode in {"draw_wire", "draw_leader", "draw_dimension", "draw_table", "place_connector"}:
            self._clear_selection()
            self.load_selection_properties_to_panel()
            if synced_wire_defaults:
                self.status("Draad tekenen: eigenschappenpaneel gebruikt als stijl voor nieuwe draden.")
            elif synced_leader_defaults:
                self.status("Leader tekenen: eigenschappenpaneel gebruikt als stijl voor nieuwe leaders.")
            elif synced_dimension_defaults:
                self.status("Maatlijn tekenen: eigenschappenpaneel gebruikt als stijl voor nieuwe maatlijnen.")
            elif mode == "draw_dimension":
                self.status("Maatlijn: klik het eerste meetpunt (snap naar connector/draadeinden).")
        self.redraw()

    def cancel_temporary_action(self):
        self.temp_wire_points = []
        self.temp_wire_anchor_meta = None
        self.temp_wire_segment_history = []
        self.temp_leader_start = None
        self.temp_dimension_start = None
        self.temp_table_start = None
        self._last_preview_cursor_world = None
        self.drag_start_world = None
        self.drag_original_world = None
        self.drag_original_points = None
        self.drag_original_wire_points = None
        self.drag_original_items = None
        self.box_select_state = None
        self.wire_endpoint_drag_state = None
        self.leader_endpoint_drag_state = None
        self.wire_tangent_drag_state = None
        self.wire_curve_drag_state = None
        self.table_resize_state = None
        self.connector_label_drag_state = None
        self.panning = False
        self.pan_start = None
        self.redraw()

    def on_escape_key(self, _event=None):
        self.set_mode("select")
        self._clear_selection()
        self.load_selection_properties_to_panel()
        if self.canvas.winfo_exists():
            self.canvas.configure(cursor="arrow")
        self.redraw()
        self.status("Selectie gewist. Mode: Selecteer / verplaats.")
        return "break"

    def focus_properties_panel(self):
        self.side_panel_visible_var.set(True)
        if "properties" in self.panel_visible_vars:
            self.panel_visible_vars["properties"].set(True)
        if "properties" in self.panel_collapsed_vars:
            self.panel_collapsed_vars["properties"].set(False)
        self._apply_panel_layout()
        self.load_selection_properties_to_panel()
        self.after_idle(lambda: self._scroll_left_panel_widget_into_view(self.prop_target_lbl))
        self.prop_color_entry.focus_set()
        self.status("Pas eigenschappen aan in het vaste paneel en klik 'Toepassen op selectie'.")

    def _update_left_panel_scrollregion(self, _event=None):
        bbox = self.left_panel_canvas.bbox("all")
        if bbox:
            self.left_panel_canvas.configure(scrollregion=bbox)

    def _on_left_panel_canvas_configure(self, event):
        self.left_panel_canvas.itemconfigure(self.left_panel_window, width=max(1, int(event.width)))
        self._update_wrap_labels(event.width)
        self._update_left_panel_scrollregion()

    def _left_panel_can_scroll(self) -> bool:
        bbox = self.left_panel_canvas.bbox("all")
        if not bbox:
            return False
        return (bbox[3] - bbox[1]) > self.left_panel_canvas.winfo_height()

    def _on_left_panel_mousewheel(self, event):
        if not self._left_panel_can_scroll():
            return None
        if getattr(event, "delta", 0):
            steps = -int(event.delta / 120)
            if steps == 0:
                steps = -1 if event.delta > 0 else 1
        elif getattr(event, "num", None) == 4:
            steps = -1
        elif getattr(event, "num", None) == 5:
            steps = 1
        else:
            return None
        self.left_panel_canvas.yview_scroll(steps, "units")
        return "break"

    def _bind_left_panel_scroll_handlers(self, widget):
        widget.bind("<MouseWheel>", self._on_left_panel_mousewheel, add="+")
        widget.bind("<Button-4>", self._on_left_panel_mousewheel, add="+")
        widget.bind("<Button-5>", self._on_left_panel_mousewheel, add="+")
        for child in widget.winfo_children():
            self._bind_left_panel_scroll_handlers(child)

    def _bind_panel_context_handlers(self, widget):
        widget.bind("<Button-3>", self._show_panel_context_menu, add="+")
        for child in widget.winfo_children():
            self._bind_panel_context_handlers(child)

    def _scroll_left_panel_widget_into_view(self, widget):
        self.update_idletasks()
        self._update_left_panel_scrollregion()
        widget_top = widget.winfo_rooty() - self.left_panel.winfo_rooty()
        widget_bottom = widget_top + widget.winfo_height()
        visible_top = self.left_panel_canvas.canvasy(0)
        visible_bottom = visible_top + self.left_panel_canvas.winfo_height()
        total_height = max(1, self.left_panel.winfo_height())
        if widget_top < visible_top:
            self.left_panel_canvas.yview_moveto(clamp(widget_top / total_height, 0.0, 1.0))
        elif widget_bottom > visible_bottom:
            target_top = widget_bottom - self.left_panel_canvas.winfo_height()
            self.left_panel_canvas.yview_moveto(clamp(target_top / total_height, 0.0, 1.0))

    def set_panel_color(self, color: str):
        self.prop_color_var.set(color)
        self._update_color_preview()

    def set_panel_color_b(self, color: str):
        self.prop_color_b_var.set(color)
        self._update_color_b_preview()

    def pick_color_for_panel(self):
        initial = self.prop_color_var.get().strip() or "#1f4e79"
        rgb, hex_color = colorchooser.askcolor(color=initial, title="Kies kleur", parent=self)
        if rgb is None or not hex_color:
            return
        self.set_panel_color(hex_color)

    def pick_color_b_for_panel(self):
        initial = self.prop_color_b_var.get().strip() or "#d7263d"
        rgb, hex_color = colorchooser.askcolor(color=initial, title="Kies kleur B", parent=self)
        if rgb is None or not hex_color:
            return
        self.set_panel_color_b(hex_color)

    def _update_color_preview(self):
        color = self.prop_color_var.get().strip() or "#1f4e79"
        try:
            self.prop_color_preview.configure(bg=color)
        except tk.TclError:
            self.prop_color_preview.configure(bg="#1f4e79")

    def _update_color_b_preview(self):
        color = self.prop_color_b_var.get().strip() or "#d7263d"
        try:
            self.prop_color_b_preview.configure(bg=color)
        except tk.TclError:
            self.prop_color_b_preview.configure(bg="#d7263d")

    def _set_color_controls_state(self, enabled: bool):
        state = "normal" if enabled else "disabled"
        self.prop_color_pick_btn.configure(state=state)
        for btn in self.prop_palette_buttons:
            btn.configure(state=state)

    def _set_color_b_controls_state(self, enabled: bool):
        state = "normal" if enabled else "disabled"
        self.prop_color_b_pick_btn.configure(state=state)

    def _set_prop_widget_state(self, widget: ttk.Entry, enabled: bool):
        widget.configure(state=("normal" if enabled else "disabled"))

    def _set_leader_text_controls_state(self, enabled: bool):
        state = "normal" if enabled else "disabled"
        self.prop_leader_text_size_entry.configure(state=state)
        self.prop_leader_text_box_check.configure(state=state)

    def _set_dimension_controls_state(self, enabled: bool):
        state = "normal" if enabled else "disabled"
        combo_state = "readonly" if enabled else "disabled"
        self.prop_dim_orientation_combo.configure(state=combo_state)
        self.prop_dim_offset_entry.configure(state=state)
        self.prop_dim_tolerance_entry.configure(state=state)

    def _on_dimension_quick_edit(self):
        items = self._active_selected_items()
        if items and all(kind == "dimension" for kind, _ident in items):
            self.apply_properties_from_panel()

    def _set_scale_property_label(self, text: str):
        if hasattr(self, "prop_scale_label"):
            self.prop_scale_label.configure(text=text)

    def _set_text_property_label(self, text: str):
        if hasattr(self, "prop_text_label"):
            self.prop_text_label.configure(text=text)

    def _set_property_hint(self, text: str):
        if hasattr(self, "property_hint_var"):
            self.property_hint_var.set(text)

    def _capture_property_row_widgets(self):
        # Veld-widgets worden tijdens de opbouw geregistreerd in _field_widgets.
        # Deze methode blijft als no-op bestaan voor compatibiliteit met de bouwflow.
        if not hasattr(self, "_field_widgets"):
            self._field_widgets = {}
            self._field_block = {}
            self._property_blocks = []

    def _property_widgets_for_row(self, row: int) -> List[object]:
        return list(getattr(self, "_field_widgets", {}).get(row, []))

    def _set_visible_property_rows(self, rows: set[int]):
        if not hasattr(self, "_field_widgets"):
            return
        visible_rows = set(rows) | {0, 22}
        self._property_visible_rows = set(visible_rows)
        for field_id, widgets in self._field_widgets.items():
            show = field_id in visible_rows
            for widget in widgets:
                if show:
                    widget.grid()
                else:
                    widget.grid_remove()
        # Klap een heel blok in als er voor deze selectie geen enkel veld in staat.
        for block in getattr(self, "_property_blocks", []):
            block_field_ids = [fid for fid, blk in self._field_block.items() if blk is block]
            if any(fid in visible_rows for fid in block_field_ids):
                block.grid()
            else:
                block.grid_remove()
        # Standaard: toon de objectnaam als label, verberg het hernoem-veld.
        # De connector-tak zet dit om wanneer precies één connector geselecteerd is.
        if getattr(self, "prop_connector_id_entry", None) is not None:
            self.prop_connector_id_entry.grid_remove()
            self.prop_target_lbl.grid()
        self.after_idle(self._update_left_panel_scrollregion)

    def _wire_style_from_panel(self) -> str:
        return wire_style_internal(self.prop_wire_style_var.get().strip())

    def _update_wire_detail_rows_for_style(self):
        if not hasattr(self, "properties_frame"):
            return
        visible_rows = getattr(self, "_property_visible_rows", set())
        if 5 not in visible_rows:
            return
        style = self._wire_style_from_panel()
        dynamic_rows = {
            6: style in {"curve", "twisted_pair_curve"},
            7: style in {"twisted_pair", "twisted_pair_curve"},
            8: style in {"twisted_pair", "twisted_pair_curve"},
            9: style in {"twisted_pair", "twisted_pair_curve"},
        }
        for row, show in dynamic_rows.items():
            for widget in self._property_widgets_for_row(row):
                if row in visible_rows and show:
                    widget.grid()
                else:
                    widget.grid_remove()
        self.after_idle(self._update_left_panel_scrollregion)

    def _wire_property_rows(self, *, include_text: bool = True, include_electrical: bool = True, include_move: bool = False) -> set[int]:
        rows = {0, 1, 2, 5, 6, 7, 8, 9, 21, 22}
        if include_text:
            rows.add(3)
        if include_electrical:
            rows.update({10, 11, 12, 13, 14, 15})
        if include_move:
            rows.update({19, 20})
        return rows

    def _set_prop_combo_state(self, widget: ttk.Combobox, enabled: bool, readonly: bool = True):
        if enabled:
            widget.configure(state=("readonly" if readonly else "normal"))
        else:
            widget.configure(state="disabled")

    def _load_wire_electrical_properties(self, wire: Optional[WirePath], enabled: bool):
        self.prop_signal_var.set(wire.signal_name if wire else "")
        self.prop_from_connector_var.set(wire.from_connector if wire else "")
        self.prop_from_pin_var.set(wire.from_pin if wire else "")
        self.prop_to_connector_var.set(wire.to_connector if wire else "")
        self.prop_to_pin_var.set(wire.to_pin if wire else "")
        self.prop_cross_section_var.set(f"{wire.cross_section_mm2:g}" if wire else "0.35")
        self.prop_length_var.set(f"{wire.length_mm:g}" if wire else "0")
        self.prop_net_var.set(wire.net_name if wire else "")
        self.prop_shielded_var.set(bool(wire.shielded) if wire else False)
        state = "normal" if enabled else "disabled"
        for widget in [
            self.prop_signal_entry,
            self.prop_from_connector_entry,
            self.prop_from_pin_entry,
            self.prop_to_connector_entry,
            self.prop_to_pin_entry,
            self.prop_cross_section_entry,
            self.prop_length_entry,
            self.prop_net_entry,
            self.prop_shielded_check,
        ]:
            widget.configure(state=state)

    def _load_connector_pin_properties(self, connector: Optional[ConnectorInstance], enabled: bool):
        self.prop_connector_part_var.set(connector.part_number if connector else "")
        self.prop_connector_pin_count_var.set(f"{max(1, connector.pin_count):g}" if connector else "1")
        self.prop_connector_pin_labels_var.set(pin_labels_text(connector.pin_labels) if connector else "")
        state = "normal" if enabled else "disabled"
        for widget in [
            self.prop_connector_part_entry,
            self.prop_connector_pin_count_entry,
            self.prop_connector_pin_labels_entry,
        ]:
            widget.configure(state=state)

    def _set_wire_move_scope(self, scope: str, update_var: bool = True, announce: bool = False):
        previous = self.wire_move_scope
        self.wire_move_scope = scope if scope in {"segment", "chain"} else "chain"
        if update_var and self.prop_wire_move_scope_var.get() != wire_move_scope_label(self.wire_move_scope):
            self.prop_wire_move_scope_var.set(wire_move_scope_label(self.wire_move_scope))
        if announce and previous != self.wire_move_scope and self.selected and self.selected[0] == "wire":
            self.load_selection_properties_to_panel()
            self.redraw()
            label = "hele lijn" if self.wire_move_scope == "chain" else "alleen segment"
            self.status(f"Draad verplaatsen: {label}.")

    def _set_wire_endpoint_drag_scope(self, scope: str, update_var: bool = True, announce: bool = False):
        previous = self.wire_endpoint_drag_scope
        self.wire_endpoint_drag_scope = scope if scope in {"single", "junction"} else "single"
        if update_var and self.prop_wire_endpoint_drag_scope_var.get() != wire_endpoint_drag_scope_label(self.wire_endpoint_drag_scope):
            self.prop_wire_endpoint_drag_scope_var.set(wire_endpoint_drag_scope_label(self.wire_endpoint_drag_scope))
        if announce and previous != self.wire_endpoint_drag_scope:
            label = "aangesloten uiteinden mee" if self.wire_endpoint_drag_scope == "junction" else "alleen dit uiteinde"
            self.status(f"Eindpunt slepen: {label}.")

    def _clear_connector_caches(self):
        self._invalidate_aa_scene()
        self._connector_world_cache.clear()
        self._connector_canvas_cache.clear()
        self._connector_local_cache.clear()
        self._connector_image_cache.clear()
        self._connector_canvas_view_signature = None
        self._image_canvas_cache.clear()
        self._canvas_image_refs = []

    def _clear_wire_geometry_caches(self):
        self._invalidate_aa_scene()
        self._wire_centerline_cache.clear()
        self._wire_polyline_cache.clear()
        self._wire_bridge_signature = None
        self._wire_bridge_cache = {}

    def _clear_wire_caches(self):
        self._clear_wire_geometry_caches()
        self._wire_connectivity_signature = None
        self._wire_connectivity_cache = {}
        self._wire_segment_index_signature = None
        self._wire_hit_segment_index = {}
        self._wire_snap_segment_index = {}

    def _on_wire_move_scope_changed(self):
        scope = wire_move_scope_internal(self.prop_wire_move_scope_var.get().strip())
        if scope == self.wire_move_scope:
            return
        self.wire_move_scope = scope
        if self.selected and self.selected[0] == "wire":
            self.load_selection_properties_to_panel()
            self.redraw()
            label = "hele lijn" if scope == "chain" else "alleen segment"
            self.status(f"Draad verplaatsen: {label}.")

    def _on_wire_endpoint_drag_scope_changed(self):
        scope = wire_endpoint_drag_scope_internal(self.prop_wire_endpoint_drag_scope_var.get().strip())
        if scope == self.wire_endpoint_drag_scope:
            return
        self.wire_endpoint_drag_scope = scope
        if self.selected and self.selected[0] == "wire":
            self.load_selection_properties_to_panel()
            self.redraw()
            label = "aangesloten uiteinden mee" if scope == "junction" else "alleen dit uiteinde"
            self.status(f"Eindpunt slepen: {label}.")

    def _on_wire_style_property_changed(self):
        self._update_wire_detail_rows_for_style()
        style = self._wire_style_from_panel()
        if style in {"twisted_pair", "twisted_pair_curve"}:
            self.status("Twisted-pair instellingen zichtbaar: Kleur B, Twist pitch en Pair gap.")

    def _load_mixed_selection_properties(self, items: set[Tuple[str, str]]):
        primary = self.selected if self.selected in items else sorted(items, key=self._selection_sort_key)[0]
        self.prop_target_var.set(f"{len(items)} objecten geselecteerd")
        self._set_property_hint("Multi-selectie: lege velden blijven ongemoeid; ingevulde velden worden op alles toegepast.")
        self._set_text_property_label("Tekst / label")
        self._set_scale_property_label("Schaal / pijl")
        self.prop_color_var.set("")
        self.prop_color_b_var.set("")
        self.prop_width_var.set("")
        self.prop_text_var.set(MULTI_SELECTION_TEXT_SENTINEL)
        self.prop_scale_var.set("")
        self.prop_wire_style_var.set(wire_style_label(self.default_wire_style))
        self.prop_curve_var.set(f"{self.default_wire_curve_offset_mm:g}")
        self.prop_twist_pitch_var.set(f"{self.default_wire_twist_pitch_mm:g}")
        self.prop_pair_gap_var.set(f"{self.default_wire_pair_gap_mm:g}")
        self._load_wire_electrical_properties(None, False)
        self._load_connector_pin_properties(None, False)

        kind, ident = primary
        if kind == "wire":
            wire = self._find_wire(ident)
            if wire:
                self.prop_color_var.set(wire.color)
                self.prop_color_b_var.set(wire.color_b)
                self.prop_width_var.set(f"{wire.width_mm:g}")
                self.prop_wire_style_var.set(wire_style_label(wire.style))
                self.prop_curve_var.set(f"{wire.curve_offset_mm:g}")
                self.prop_twist_pitch_var.set(f"{wire.twist_pitch_mm:g}")
                self.prop_pair_gap_var.set(f"{wire.pair_gap_mm:g}")
        elif kind == "leader":
            leader = self._find_leader(ident)
            if leader:
                self.prop_color_var.set(leader.color)
                self.prop_width_var.set(f"{leader.width_mm:g}")
                self.prop_scale_var.set(f"{leader.arrow_size_mm:g}")
        elif kind == "connector":
            conn = self._find_connector(ident)
            if conn:
                self.prop_color_var.set(conn.line_color)
                self.prop_width_var.set(f"{conn.line_width_mm:g}")
                self.prop_scale_var.set(f"{conn.scale:g}")
        elif kind == "table":
            table = self._find_table(ident)
            if table:
                self.prop_color_var.set(table.border_color)
                self.prop_width_var.set(f"{table.border_width_mm:g}")
        elif kind == "text":
            note = self._find_text_note(ident)
            if note:
                self.prop_color_var.set(note.color)
                self.prop_width_var.set(f"{note.font_size_pt:g}")
        elif kind == "image":
            note = self._find_image_note(ident)
            if note:
                self.prop_scale_var.set(f"{note.scale:g}")

        self.prop_width_var.set("")
        self.prop_scale_var.set("")
        kinds = {kind for kind, _ident in items}
        has_color = bool(kinds & {"wire", "leader", "dimension", "connector", "table", "text"})
        has_text = bool(kinds & {"wire", "leader", "connector", "text"})
        has_scale = bool(kinds & {"connector", "image", "leader", "dimension"})
        has_wire = "wire" in kinds
        rows = {0, 22}
        if has_color:
            rows.update({1, 21})
        if kinds & {"wire", "leader", "dimension", "connector", "table", "text"}:
            rows.add(2)
        if has_text:
            rows.add(3)
        if has_scale:
            rows.add(4)
        if has_wire:
            rows.add(7)
        self._set_visible_property_rows(rows)
        self._set_prop_widget_state(self.prop_color_entry, has_color)
        self._set_color_controls_state(has_color)
        self._set_prop_widget_state(self.prop_color_b_entry, has_wire)
        self._set_color_b_controls_state(has_wire)
        self._set_prop_widget_state(self.prop_width_entry, bool(kinds & {"wire", "leader", "dimension", "connector", "table", "text"}))
        self._set_prop_widget_state(self.prop_text_entry, has_text)
        self._set_prop_widget_state(self.prop_scale_entry, has_scale)
        self._set_prop_combo_state(self.prop_wire_style_combo, False)
        self._set_prop_widget_state(self.prop_curve_entry, False)
        self._set_prop_widget_state(self.prop_twist_pitch_entry, False)
        self._set_prop_widget_state(self.prop_pair_gap_entry, False)
        self._set_wire_move_scope(self.wire_move_scope)
        self._set_prop_combo_state(self.prop_wire_move_scope_combo, False)
        self._set_wire_endpoint_drag_scope(self.wire_endpoint_drag_scope)
        self._set_prop_combo_state(self.prop_wire_endpoint_drag_scope_combo, False)
        self.prop_apply_btn.configure(state="normal")
        self.prop_default_btn.configure(state="normal")
        self._update_color_preview()
        self._update_color_b_preview()

    def load_selection_properties_to_panel(self):
        items = self._active_selected_items()
        if not items and self.selected:
            self._clear_selection()
        if len(items) > 1 and len({kind for kind, _ident in items}) > 1:
            self._load_mixed_selection_properties(items)
            return

        selected = self.selected
        if not selected:
            if self.mode == "draw_leader":
                self._set_property_hint("Nieuwe leaders gebruiken deze kleur, lijndikte, tekst en pijlgrootte.")
                self._set_text_property_label("Leader tekst")
                self._set_scale_property_label("Pijlgrootte mm")
                self._set_visible_property_rows({0, 1, 2, 3, 4, 21, 22, 23, 24})
                self.prop_target_var.set("Leader tekenen (stijl nieuwe leader)")
                self.prop_color_var.set(self.default_leader_color)
                self.prop_color_b_var.set(self.default_wire_color_b)
                self.prop_width_var.set(f"{self.default_leader_width_mm:g}")
                self.prop_text_var.set("")
                self.prop_scale_var.set(f"{self.default_leader_arrow_size_mm:g}")
                self.prop_leader_text_size_var.set(f"{self.default_leader_text_size_pt:g}")
                self.prop_leader_text_box_var.set(bool(self.default_leader_text_box))
                self.prop_wire_style_var.set("Recht")
                self.prop_curve_var.set("0")
                self.prop_twist_pitch_var.set("10")
                self.prop_pair_gap_var.set("2.8")
                self._load_wire_electrical_properties(None, False)
                self._load_connector_pin_properties(None, False)
                self._set_prop_widget_state(self.prop_color_entry, True)
                self._set_color_controls_state(True)
                self._set_prop_widget_state(self.prop_color_b_entry, False)
                self._set_color_b_controls_state(False)
                self._set_prop_widget_state(self.prop_width_entry, True)
                self._set_prop_widget_state(self.prop_text_entry, True)
                self._set_prop_widget_state(self.prop_scale_entry, True)
                self._set_leader_text_controls_state(True)
                self._set_prop_combo_state(self.prop_wire_style_combo, False)
                self._set_prop_widget_state(self.prop_curve_entry, False)
                self._set_prop_widget_state(self.prop_twist_pitch_entry, False)
                self._set_prop_widget_state(self.prop_pair_gap_entry, False)
                self._set_prop_combo_state(self.prop_wire_move_scope_combo, False)
                self._set_prop_combo_state(self.prop_wire_endpoint_drag_scope_combo, False)
                self.prop_apply_btn.configure(state="disabled")
                self.prop_default_btn.configure(state="normal")
                self._update_color_preview()
                self._update_color_b_preview()
                return

            if self.mode == "draw_dimension":
                self._set_property_hint("Nieuwe maatlijnen gebruiken deze stijl. Klik twee meetpunten; lengte volgt automatisch.")
                self._set_text_property_label("Maatwaarde (leeg=auto)")
                self._set_scale_property_label("Pijlgrootte mm")
                self._set_visible_property_rows({0, 1, 2, 3, 4, 21, 22, 23, 25, 26, 27})
                self.prop_target_var.set("Maatlijn tekenen (stijl nieuwe maatlijn)")
                self.prop_color_var.set(self.default_dimension_color)
                self.prop_color_b_var.set(self.default_wire_color_b)
                self.prop_width_var.set(f"{self.default_dimension_line_width_mm:g}")
                self.prop_text_var.set("")
                self.prop_scale_var.set(f"{self.default_dimension_arrow_size_mm:g}")
                self.prop_leader_text_size_var.set(f"{self.default_dimension_text_size_pt:g}")
                self.prop_dim_orientation_var.set(dimension_orientation_label(self.default_dimension_orientation))
                self.prop_dim_offset_var.set(f"{self.default_dimension_offset_mm:g}")
                self.prop_dim_tolerance_var.set(self.default_dimension_tolerance)
                self._load_wire_electrical_properties(None, False)
                self._load_connector_pin_properties(None, False)
                self._set_prop_widget_state(self.prop_color_entry, True)
                self._set_color_controls_state(True)
                self._set_prop_widget_state(self.prop_color_b_entry, False)
                self._set_color_b_controls_state(False)
                self._set_prop_widget_state(self.prop_width_entry, True)
                self._set_prop_widget_state(self.prop_text_entry, True)
                self._set_prop_widget_state(self.prop_scale_entry, True)
                self._set_leader_text_controls_state(False)
                self.prop_leader_text_size_entry.configure(state="normal")
                self._set_dimension_controls_state(True)
                self._set_prop_combo_state(self.prop_wire_style_combo, False)
                self._set_prop_widget_state(self.prop_curve_entry, False)
                self._set_prop_widget_state(self.prop_twist_pitch_entry, False)
                self._set_prop_widget_state(self.prop_pair_gap_entry, False)
                self._set_prop_combo_state(self.prop_wire_move_scope_combo, False)
                self._set_prop_combo_state(self.prop_wire_endpoint_drag_scope_combo, False)
                self.prop_apply_btn.configure(state="disabled")
                self.prop_default_btn.configure(state="normal")
                self._update_color_preview()
                self._update_color_b_preview()
                return

            if self.mode == "draw_table":
                self._set_property_hint("Nieuwe tabellen gebruiken deze randkleur en lijndikte.")
                self._set_text_property_label("Tekst / label")
                self._set_scale_property_label("Schaal")
                self._set_visible_property_rows({0, 1, 2, 21, 22})
                self.prop_target_var.set("Tabel plaatsen (borderstijl)")
                self.prop_color_var.set(self.default_table_border_color)
                self.prop_color_b_var.set(self.default_wire_color_b)
                self.prop_width_var.set(f"{self.default_table_border_width_mm:g}")
                self.prop_text_var.set("")
                self.prop_scale_var.set("")
                self.prop_wire_style_var.set("Recht")
                self.prop_curve_var.set("0")
                self.prop_twist_pitch_var.set("10")
                self.prop_pair_gap_var.set("2.8")
                self._load_wire_electrical_properties(None, False)
                self._load_connector_pin_properties(None, False)
                self._set_prop_widget_state(self.prop_color_entry, True)
                self._set_color_controls_state(True)
                self._set_prop_widget_state(self.prop_color_b_entry, False)
                self._set_color_b_controls_state(False)
                self._set_prop_widget_state(self.prop_width_entry, True)
                self._set_prop_widget_state(self.prop_text_entry, False)
                self._set_prop_widget_state(self.prop_scale_entry, False)
                self._set_prop_combo_state(self.prop_wire_style_combo, False)
                self._set_prop_widget_state(self.prop_curve_entry, False)
                self._set_prop_widget_state(self.prop_twist_pitch_entry, False)
                self._set_prop_widget_state(self.prop_pair_gap_entry, False)
                self._set_prop_combo_state(self.prop_wire_move_scope_combo, False)
                self._set_prop_combo_state(self.prop_wire_endpoint_drag_scope_combo, False)
                self.prop_apply_btn.configure(state="disabled")
                self.prop_default_btn.configure(state="normal")
                self._update_color_preview()
                self._update_color_b_preview()
                return

            if self.mode == "place_connector":
                self._set_property_hint("Nieuwe connectors gebruiken deze lijnstijl; kies een symbool in de bibliotheek.")
                self._set_text_property_label("Notitie")
                self._set_scale_property_label("Schaal")
                self._set_visible_property_rows({0, 1, 2, 21, 22})
                self.prop_target_var.set("Connector plaatsen (lijnstijl)")
                self.prop_color_var.set(self.default_connector_line_color)
                self.prop_color_b_var.set(self.default_wire_color_b)
                self.prop_width_var.set(f"{self.default_connector_line_width_mm:g}")
                self.prop_text_var.set("")
                self.prop_scale_var.set("")
                self.prop_wire_style_var.set("Recht")
                self.prop_curve_var.set("0")
                self.prop_twist_pitch_var.set("10")
                self.prop_pair_gap_var.set("2.8")
                self._load_wire_electrical_properties(None, False)
                self._load_connector_pin_properties(None, False)
                self._set_prop_widget_state(self.prop_color_entry, True)
                self._set_color_controls_state(True)
                self._set_prop_widget_state(self.prop_color_b_entry, False)
                self._set_color_b_controls_state(False)
                self._set_prop_widget_state(self.prop_width_entry, True)
                self._set_prop_widget_state(self.prop_text_entry, False)
                self._set_prop_widget_state(self.prop_scale_entry, False)
                self._set_prop_combo_state(self.prop_wire_style_combo, False)
                self._set_prop_widget_state(self.prop_curve_entry, False)
                self._set_prop_widget_state(self.prop_twist_pitch_entry, False)
                self._set_prop_widget_state(self.prop_pair_gap_entry, False)
                self._set_prop_combo_state(self.prop_wire_move_scope_combo, False)
                self._set_prop_combo_state(self.prop_wire_endpoint_drag_scope_combo, False)
                self.prop_apply_btn.configure(state="disabled")
                self.prop_default_btn.configure(state="normal")
                self._update_color_preview()
                self._update_color_b_preview()
                return

            self._set_scale_property_label("Schaal")
            self._set_text_property_label("Draadlabel")
            self._set_property_hint("Nieuwe draden gebruiken deze stijl. Selecteer een object om objecteigenschappen te bewerken.")
            self._set_visible_property_rows(self._wire_property_rows(include_text=False, include_electrical=False, include_move=False))
            target_text = "Draad tekenen (stijl nieuwe draad)" if self.mode == "draw_wire" else "Geen selectie (default draadstijl)"
            self.prop_target_var.set(target_text)
            self.prop_color_var.set(self.default_wire_color)
            self.prop_color_b_var.set(self.default_wire_color_b)
            self.prop_width_var.set(f"{self.default_wire_width_mm:g}")
            self.prop_text_var.set("")
            self.prop_scale_var.set("")
            self.prop_wire_style_var.set(wire_style_label(self.default_wire_style))
            self.prop_curve_var.set(f"{self.default_wire_curve_offset_mm:g}")
            self.prop_twist_pitch_var.set(f"{self.default_wire_twist_pitch_mm:g}")
            self.prop_pair_gap_var.set(f"{self.default_wire_pair_gap_mm:g}")
            self.prop_leader_text_size_var.set(f"{self.default_leader_text_size_pt:g}")
            self.prop_leader_text_box_var.set(bool(self.default_leader_text_box))
            self._load_wire_electrical_properties(None, False)
            self._load_connector_pin_properties(None, False)
            self._set_prop_widget_state(self.prop_color_entry, True)
            self._set_color_controls_state(True)
            self._set_prop_widget_state(self.prop_color_b_entry, True)
            self._set_color_b_controls_state(True)
            self._set_prop_widget_state(self.prop_width_entry, True)
            self._set_prop_widget_state(self.prop_text_entry, False)
            self._set_prop_widget_state(self.prop_scale_entry, False)
            self._set_prop_combo_state(self.prop_wire_style_combo, True)
            self._set_prop_widget_state(self.prop_curve_entry, True)
            self._set_prop_widget_state(self.prop_twist_pitch_entry, True)
            self._set_prop_widget_state(self.prop_pair_gap_entry, True)
            self._set_leader_text_controls_state(False)
            self._set_wire_move_scope(self.wire_move_scope)
            self._set_prop_combo_state(self.prop_wire_move_scope_combo, False)
            self._set_wire_endpoint_drag_scope(self.wire_endpoint_drag_scope)
            self._set_prop_combo_state(self.prop_wire_endpoint_drag_scope_combo, False)
            self.prop_apply_btn.configure(state="disabled")
            self.prop_default_btn.configure(state="normal")
            self._update_color_preview()
            self._update_color_b_preview()
            self._update_wire_detail_rows_for_style()
            return

        kind, ident = selected
        self._set_property_hint("Wijzig velden en klik op Toepassen op selectie; contextopties staan onder rechtermuisknop.")
        self._set_scale_property_label("Schaal")
        self._set_text_property_label("Tekst / label")
        self.prop_apply_btn.configure(state="normal")
        self.prop_default_btn.configure(state="normal")
        self._load_wire_electrical_properties(None, False)
        self._load_connector_pin_properties(None, False)

        if kind == "wire":
            wire = self._find_wire(ident)
            if not wire:
                return
            self._set_visible_property_rows(self._wire_property_rows(include_text=True, include_electrical=True, include_move=True))
            self._set_text_property_label("Draadlabel")
            selected_count = len(self._valid_selected_wire_ids())
            chain_count = len(self._connected_wire_ids(wire.wire_id))
            if selected_count > 1:
                self.prop_target_var.set(f"{selected_count} draadsegmenten geselecteerd")
            else:
                self.prop_target_var.set(f"Wire {wire.wire_id} ({chain_count} segmenten gekoppeld)")
            self.prop_color_var.set(wire.color)
            self.prop_color_b_var.set(wire.color_b)
            self.prop_width_var.set(f"{wire.width_mm:g}")
            self.prop_text_var.set(wire.label)
            if selected_count > 1:
                self.prop_text_var.set(MULTI_SELECTION_TEXT_SENTINEL)
            self.prop_scale_var.set("")
            self.prop_wire_style_var.set(wire_style_label(wire.style))
            self.prop_curve_var.set(f"{wire.curve_offset_mm:g}")
            self.prop_twist_pitch_var.set(f"{wire.twist_pitch_mm:g}")
            self.prop_pair_gap_var.set(f"{wire.pair_gap_mm:g}")
            self._load_wire_electrical_properties(wire, True)
            self._set_prop_widget_state(self.prop_color_entry, True)
            self._set_color_controls_state(True)
            self._set_prop_widget_state(self.prop_color_b_entry, True)
            self._set_color_b_controls_state(True)
            self._set_prop_widget_state(self.prop_width_entry, True)
            self._set_prop_widget_state(self.prop_text_entry, True)
            self._set_prop_widget_state(self.prop_scale_entry, False)
            self._set_prop_combo_state(self.prop_wire_style_combo, True)
            self._set_prop_widget_state(self.prop_curve_entry, True)
            self._set_prop_widget_state(self.prop_twist_pitch_entry, True)
            self._set_prop_widget_state(self.prop_pair_gap_entry, True)
            self._set_leader_text_controls_state(False)
            self._set_wire_move_scope(self.wire_move_scope)
            self._set_prop_combo_state(self.prop_wire_move_scope_combo, True)
            self._set_wire_endpoint_drag_scope(self.wire_endpoint_drag_scope)
            self._set_prop_combo_state(self.prop_wire_endpoint_drag_scope_combo, True)
            self._update_color_preview()
            self._update_color_b_preview()
            self._update_wire_detail_rows_for_style()
            return

        if kind == "leader":
            leader = self._find_leader(ident)
            if not leader:
                return
            self._set_visible_property_rows({0, 1, 2, 3, 4, 21, 22, 23, 24})
            self._set_text_property_label("Leader tekst")
            selected_count = self._selection_count_for_kind("leader")
            self.prop_target_var.set(f"{selected_count} leaders geselecteerd" if selected_count > 1 else f"Leader {leader.leader_id}")
            self.prop_color_var.set(leader.color)
            self.prop_color_b_var.set(self.default_wire_color_b)
            self.prop_width_var.set(f"{leader.width_mm:g}")
            self.prop_text_var.set(leader.text)
            if selected_count > 1:
                self.prop_text_var.set(MULTI_SELECTION_TEXT_SENTINEL)
            self._set_scale_property_label("Pijlgrootte mm")
            self.prop_scale_var.set(f"{leader.arrow_size_mm:g}")
            self.prop_leader_text_size_var.set(f"{leader.text_size_pt:g}")
            self.prop_leader_text_box_var.set(bool(leader.text_box))
            self.prop_wire_style_var.set("Recht")
            self.prop_curve_var.set("0")
            self.prop_twist_pitch_var.set("10")
            self.prop_pair_gap_var.set("2.8")
            self._load_connector_pin_properties(None, False)
            self._set_prop_widget_state(self.prop_color_entry, True)
            self._set_color_controls_state(True)
            self._set_prop_widget_state(self.prop_color_b_entry, False)
            self._set_color_b_controls_state(False)
            self._set_prop_widget_state(self.prop_width_entry, True)
            self._set_prop_widget_state(self.prop_text_entry, True)
            self._set_prop_widget_state(self.prop_scale_entry, True)
            self._set_leader_text_controls_state(True)
            self._set_prop_combo_state(self.prop_wire_style_combo, False)
            self._set_prop_widget_state(self.prop_curve_entry, False)
            self._set_prop_widget_state(self.prop_twist_pitch_entry, False)
            self._set_prop_widget_state(self.prop_pair_gap_entry, False)
            self._set_wire_move_scope(self.wire_move_scope)
            self._set_prop_combo_state(self.prop_wire_move_scope_combo, False)
            self._set_wire_endpoint_drag_scope(self.wire_endpoint_drag_scope)
            self._set_prop_combo_state(self.prop_wire_endpoint_drag_scope_combo, False)
            self._update_color_preview()
            self._update_color_b_preview()
            return

        if kind == "dimension":
            dim = self._find_dimension(ident)
            if not dim:
                return
            self._set_visible_property_rows({0, 1, 2, 3, 4, 21, 22, 23, 25, 26, 27})
            self._set_text_property_label("Maatwaarde (leeg=auto)")
            self._set_scale_property_label("Pijlgrootte mm")
            selected_count = self._selection_count_for_kind("dimension")
            self.prop_target_var.set(f"{selected_count} maatlijnen geselecteerd" if selected_count > 1 else f"Maatlijn {dim.dim_id}")
            self.prop_color_var.set(dim.color)
            self.prop_color_b_var.set(self.default_wire_color_b)
            self.prop_width_var.set(f"{dim.line_width_mm:g}")
            self.prop_text_var.set(dim.override_text)
            if selected_count > 1:
                self.prop_text_var.set(MULTI_SELECTION_TEXT_SENTINEL)
            self.prop_scale_var.set(f"{dim.arrow_size_mm:g}")
            self.prop_leader_text_size_var.set(f"{dim.text_size_pt:g}")
            self.prop_dim_orientation_var.set(dimension_orientation_label(dim.orientation))
            self.prop_dim_offset_var.set(f"{dim.offset_mm:g}")
            self.prop_dim_tolerance_var.set(dim.tolerance)
            self.prop_wire_style_var.set("Recht")
            self._load_connector_pin_properties(None, False)
            self._set_prop_widget_state(self.prop_color_entry, True)
            self._set_color_controls_state(True)
            self._set_prop_widget_state(self.prop_color_b_entry, False)
            self._set_color_b_controls_state(False)
            self._set_prop_widget_state(self.prop_width_entry, True)
            self._set_prop_widget_state(self.prop_text_entry, True)
            self._set_prop_widget_state(self.prop_scale_entry, True)
            self._set_leader_text_controls_state(False)
            self.prop_leader_text_size_entry.configure(state="normal")
            self._set_dimension_controls_state(True)
            self._set_prop_combo_state(self.prop_wire_style_combo, False)
            self._set_prop_widget_state(self.prop_curve_entry, False)
            self._set_prop_widget_state(self.prop_twist_pitch_entry, False)
            self._set_prop_widget_state(self.prop_pair_gap_entry, False)
            self._set_prop_combo_state(self.prop_wire_move_scope_combo, False)
            self._set_prop_combo_state(self.prop_wire_endpoint_drag_scope_combo, False)
            self._update_color_preview()
            self._update_color_b_preview()
            return

        if kind == "connector":
            conn = self._find_connector(ident)
            if not conn:
                return
            self._set_visible_property_rows({0, 1, 2, 3, 4, 16, 17, 18, 21, 22, 28})
            self._set_text_property_label("Notitie")
            selected_count = self._selection_count_for_kind("connector")
            self.prop_target_var.set(f"{selected_count} connectors geselecteerd" if selected_count > 1 else f"Connector {conn.connector_id}")
            if selected_count > 1:
                # Bij meerdere connectors: toon teller-label, geen hernoem-veld.
                self.prop_connector_id_entry.grid_remove()
                self.prop_target_lbl.grid()
            else:
                # Eén connector: maak de naam bewerkbaar via een invoerveld op rij 0.
                self.prop_connector_id_var.set(conn.connector_id)
                self.prop_target_lbl.grid_remove()
                self.prop_connector_id_entry.grid()
            self.prop_connector_label_dx_var.set(f"{conn.label_dx_mm:g}")
            self.prop_connector_label_dy_var.set(f"{conn.label_dy_mm:g}")
            self.prop_color_var.set(conn.line_color)
            self.prop_color_b_var.set(self.default_wire_color_b)
            self.prop_width_var.set(f"{conn.line_width_mm:g}")
            self.prop_text_var.set(conn.note)
            if selected_count > 1:
                self.prop_text_var.set(MULTI_SELECTION_TEXT_SENTINEL)
            self.prop_scale_var.set(f"{conn.scale:g}")
            self.prop_wire_style_var.set("Recht")
            self.prop_curve_var.set("0")
            self.prop_twist_pitch_var.set("10")
            self.prop_pair_gap_var.set("2.8")
            self._load_connector_pin_properties(conn, True)
            self._set_prop_widget_state(self.prop_color_entry, True)
            self._set_color_controls_state(True)
            self._set_prop_widget_state(self.prop_color_b_entry, False)
            self._set_color_b_controls_state(False)
            self._set_prop_widget_state(self.prop_width_entry, True)
            self._set_prop_widget_state(self.prop_text_entry, True)
            self._set_prop_widget_state(self.prop_scale_entry, True)
            self._set_prop_widget_state(self.prop_connector_label_dx_entry, True)
            self._set_prop_widget_state(self.prop_connector_label_dy_entry, True)
            self._set_prop_combo_state(self.prop_wire_style_combo, False)
            self._set_prop_widget_state(self.prop_curve_entry, False)
            self._set_prop_widget_state(self.prop_twist_pitch_entry, False)
            self._set_prop_widget_state(self.prop_pair_gap_entry, False)
            self._set_wire_move_scope(self.wire_move_scope)
            self._set_prop_combo_state(self.prop_wire_move_scope_combo, False)
            self._set_wire_endpoint_drag_scope(self.wire_endpoint_drag_scope)
            self._set_prop_combo_state(self.prop_wire_endpoint_drag_scope_combo, False)
            self._update_color_preview()
            self._update_color_b_preview()
            return

        if kind == "table":
            table = self._find_table(ident)
            if not table:
                return
            self._set_visible_property_rows({0, 1, 2, 21, 22})
            self._set_text_property_label("Tekst / label")
            selected_count = self._selection_count_for_kind("table")
            label = "borders/tabellen" if selected_count > 1 else ("Border" if table.is_border else "Tabel")
            self.prop_target_var.set(f"{selected_count} {label} geselecteerd" if selected_count > 1 else f"{label} {table.table_id}")
            self.prop_color_var.set(table.border_color)
            self.prop_color_b_var.set(self.default_wire_color_b)
            self.prop_width_var.set(f"{table.border_width_mm:g}")
            self.prop_text_var.set("")
            self.prop_scale_var.set("")
            self.prop_wire_style_var.set("Recht")
            self.prop_curve_var.set("0")
            self.prop_twist_pitch_var.set("10")
            self.prop_pair_gap_var.set("2.8")
            self._set_prop_widget_state(self.prop_color_entry, True)
            self._set_color_controls_state(True)
            self._set_prop_widget_state(self.prop_color_b_entry, False)
            self._set_color_b_controls_state(False)
            self._set_prop_widget_state(self.prop_width_entry, True)
            self._set_prop_widget_state(self.prop_text_entry, False)
            self._set_prop_widget_state(self.prop_scale_entry, False)
            self._set_prop_combo_state(self.prop_wire_style_combo, False)
            self._set_prop_widget_state(self.prop_curve_entry, False)
            self._set_prop_widget_state(self.prop_twist_pitch_entry, False)
            self._set_prop_widget_state(self.prop_pair_gap_entry, False)
            self._set_wire_move_scope(self.wire_move_scope)
            self._set_prop_combo_state(self.prop_wire_move_scope_combo, False)
            self._set_wire_endpoint_drag_scope(self.wire_endpoint_drag_scope)
            self._set_prop_combo_state(self.prop_wire_endpoint_drag_scope_combo, False)
            self._update_color_preview()
            self._update_color_b_preview()
            return

        if kind == "text":
            note = self._find_text_note(ident)
            if not note:
                return
            self._set_visible_property_rows({0, 1, 2, 3, 21, 22})
            self._set_text_property_label("Tekst")
            selected_count = self._selection_count_for_kind("text")
            self.prop_target_var.set(f"{selected_count} teksten geselecteerd" if selected_count > 1 else f"Tekst {note.note_id}")
            self.prop_color_var.set(note.color)
            self.prop_color_b_var.set(self.default_wire_color_b)
            self.prop_width_var.set(f"{note.font_size_pt:g}")
            self.prop_text_var.set(note.text)
            if selected_count > 1:
                self.prop_text_var.set(MULTI_SELECTION_TEXT_SENTINEL)
            self.prop_scale_var.set("")
            self.prop_wire_style_var.set("Recht")
            self.prop_curve_var.set("0")
            self.prop_twist_pitch_var.set("10")
            self.prop_pair_gap_var.set("2.8")
            self._set_prop_widget_state(self.prop_color_entry, True)
            self._set_color_controls_state(True)
            self._set_prop_widget_state(self.prop_color_b_entry, False)
            self._set_color_b_controls_state(False)
            self._set_prop_widget_state(self.prop_width_entry, True)
            self._set_prop_widget_state(self.prop_text_entry, True)
            self._set_prop_widget_state(self.prop_scale_entry, False)
            self._set_prop_combo_state(self.prop_wire_style_combo, False)
            self._set_prop_widget_state(self.prop_curve_entry, False)
            self._set_prop_widget_state(self.prop_twist_pitch_entry, False)
            self._set_prop_widget_state(self.prop_pair_gap_entry, False)
            self._set_prop_combo_state(self.prop_wire_move_scope_combo, False)
            self._set_wire_endpoint_drag_scope(self.wire_endpoint_drag_scope)
            self._set_prop_combo_state(self.prop_wire_endpoint_drag_scope_combo, False)
            self._update_color_preview()
            self._update_color_b_preview()
            return

        if kind == "image":
            note = self._find_image_note(ident)
            if not note:
                return
            self._set_visible_property_rows({0, 4, 22})
            self._set_text_property_label("Bestand")
            selected_count = self._selection_count_for_kind("image")
            self.prop_target_var.set(f"{selected_count} afbeeldingen geselecteerd" if selected_count > 1 else f"Afbeelding {note.image_id}")
            self.prop_color_var.set(self.default_wire_color)
            self.prop_color_b_var.set(self.default_wire_color_b)
            self.prop_width_var.set("")
            self.prop_text_var.set(Path(note.source_path).name if note.source_path else "")
            self.prop_scale_var.set(f"{note.scale:g}")
            self.prop_wire_style_var.set("Recht")
            self.prop_curve_var.set("0")
            self.prop_twist_pitch_var.set("10")
            self.prop_pair_gap_var.set("2.8")
            self._set_prop_widget_state(self.prop_color_entry, False)
            self._set_color_controls_state(False)
            self._set_prop_widget_state(self.prop_color_b_entry, False)
            self._set_color_b_controls_state(False)
            self._set_prop_widget_state(self.prop_width_entry, False)
            self._set_prop_widget_state(self.prop_text_entry, False)
            self._set_prop_widget_state(self.prop_scale_entry, True)
            self._set_prop_combo_state(self.prop_wire_style_combo, False)
            self._set_prop_widget_state(self.prop_curve_entry, False)
            self._set_prop_widget_state(self.prop_twist_pitch_entry, False)
            self._set_prop_widget_state(self.prop_pair_gap_entry, False)
            self._set_prop_combo_state(self.prop_wire_move_scope_combo, False)
            self._set_wire_endpoint_drag_scope(self.wire_endpoint_drag_scope)
            self._set_prop_combo_state(self.prop_wire_endpoint_drag_scope_combo, False)
            self._update_color_preview()
            self._update_color_b_preview()
            return

        self.prop_target_var.set(f"{kind} {ident}")
        self._set_wire_move_scope(self.wire_move_scope)
        self._set_prop_combo_state(self.prop_wire_move_scope_combo, False)
        self._set_wire_endpoint_drag_scope(self.wire_endpoint_drag_scope)
        self._set_prop_combo_state(self.prop_wire_endpoint_drag_scope_combo, False)
        self._update_color_preview()
        self._update_color_b_preview()

    def apply_properties_from_panel(self):
        items = self._active_selected_items()
        if not items:
            self.status("Geen selectie om eigenschappen op toe te passen.")
            return
        before = self._capture_before_change()
        color = self.prop_color_var.get().strip()
        color_b = self.prop_color_b_var.get().strip()
        width_raw = self.prop_width_var.get().strip()
        scale_raw = self.prop_scale_var.get().strip()
        width = max(0.05, try_float(width_raw, 0.5))
        text_value = self.prop_text_var.get()
        scale_val = max(0.05, try_float(scale_raw, 1.0))
        wire_style = wire_style_internal(self.prop_wire_style_var.get().strip())
        curve_mm = try_float(self.prop_curve_var.get(), 8.0)
        twist_pitch = max(1.0, try_float(self.prop_twist_pitch_var.get(), 10.0))
        pair_gap = max(0.2, try_float(self.prop_pair_gap_var.get(), 2.8))
        cross_section = max(0.0, try_float(self.prop_cross_section_var.get(), 0.0))
        length_mm = max(0.0, try_float(self.prop_length_var.get(), 0.0))
        leader_text_size = max(4.0, try_float(self.prop_leader_text_size_var.get(), DEFAULT_LEADER_TEXT_SIZE_PT))
        leader_text_box = bool(self.prop_leader_text_box_var.get())
        dim_orientation = dimension_orientation_internal(self.prop_dim_orientation_var.get().strip())
        dim_offset = try_float(self.prop_dim_offset_var.get(), self.default_dimension_offset_mm)
        dim_tolerance = self.prop_dim_tolerance_var.get().strip()
        kinds = {kind for kind, _ident in items}
        homogeneous = len(kinds) == 1
        apply_width = bool(width_raw)
        apply_scale = bool(scale_raw)
        apply_text = not (len(items) > 1 and text_value.strip() == MULTI_SELECTION_TEXT_SENTINEL)
        apply_wire_detail = homogeneous and "wire" in kinds
        apply_connector_detail = homogeneous and "connector" in kinds
        apply_leader_detail = homogeneous and "leader" in kinds
        apply_dimension_detail = homogeneous and "dimension" in kinds

        self._apply_properties_to_items(
            items,
            color=color,
            color_b=color_b,
            width=width,
            text_value=text_value,
            scale_val=scale_val,
            wire_style=wire_style,
            curve_mm=curve_mm,
            twist_pitch=twist_pitch,
            pair_gap=pair_gap,
            cross_section=cross_section,
            length_mm=length_mm,
            leader_text_size=leader_text_size,
            leader_text_box=leader_text_box,
            dim_orientation=dim_orientation,
            dim_offset=dim_offset,
            dim_tolerance=dim_tolerance,
            apply_width=apply_width,
            apply_scale=apply_scale,
            apply_text=apply_text,
            apply_wire_detail=apply_wire_detail,
            apply_connector_detail=apply_connector_detail,
            apply_leader_detail=apply_leader_detail,
            apply_dimension_detail=apply_dimension_detail,
        )

        self.load_selection_properties_to_panel()
        self.redraw()
        self._commit_change(before, "Eigenschappen toegepast.")

    def _rename_selected_connector(self):
        """Hernoem de geselecteerde connector en werk draadverwijzingen mee."""
        if getattr(self, "_renaming_connector", False):
            return
        if not self.selected or self.selected[0] != "connector":
            return
        if self._selection_count_for_kind("connector") > 1:
            return
        conn = self._find_connector(self.selected[1])
        if not conn:
            return
        new_id = self.prop_connector_id_var.get().strip()
        old_id = conn.connector_id
        if not new_id or new_id == old_id:
            self.prop_connector_id_var.set(old_id)
            return
        if any(c.connector_id == new_id for c in self.connectors if c is not conn):
            self._show_error(f"Connector ID '{new_id}' bestaat al.")
            self.prop_connector_id_var.set(old_id)
            return
        self._renaming_connector = True
        try:
            before = self._capture_before_change()
            conn.connector_id = new_id
            for wire in self.wires:
                if wire.from_connector == old_id:
                    wire.from_connector = new_id
                if wire.to_connector == old_id:
                    wire.to_connector = new_id
            self._set_single_selection("connector", new_id)
            self.load_selection_properties_to_panel()
            self.redraw()
            self._commit_change(before, f"Connector hernoemd naar {new_id}.")
            self.status(f"Connector hernoemd: {old_id} -> {new_id}.")
        finally:
            self._renaming_connector = False

    def _apply_properties_to_items(
        self,
        items: set[Tuple[str, str]],
        *,
        color: str,
        color_b: str,
        width: float,
        text_value: str,
        scale_val: float,
        wire_style: str,
        curve_mm: float,
        twist_pitch: float,
        pair_gap: float,
        cross_section: float,
        length_mm: float,
        leader_text_size: float,
        leader_text_box: bool,
        dim_orientation: str = "horizontal",
        dim_offset: float = DEFAULT_DIMENSION_OFFSET_MM,
        dim_tolerance: str = "",
        apply_width: bool,
        apply_scale: bool,
        apply_text: bool,
        apply_wire_detail: bool,
        apply_connector_detail: bool,
        apply_leader_detail: bool,
        apply_dimension_detail: bool = False,
    ):
        touched_wires = False
        touched_connectors = False
        curve_styles = {"curve", "twisted_pair_curve"}
        wire_detail_wire_ids: set[str] = set()
        should_smooth_wire_chains = False
        for kind, ident in sorted(items, key=self._selection_sort_key):
            if kind == "wire":
                wire = self._find_wire(ident)
                if not wire:
                    continue
                previous_style = normalize_wire_style(wire.style)
                previous_curve = wire.curve_offset_mm
                if color:
                    wire.color = color
                if color_b:
                    wire.color_b = color_b
                if apply_width:
                    wire.width_mm = max(0.2, width)
                if apply_wire_detail:
                    wire.style = wire_style
                    wire.curve_offset_mm = curve_mm
                    if wire.style in {"straight", "twisted_pair"} or abs(curve_mm - previous_curve) > 1e-9:
                        wire.start_handle_offset_mm = (0.0, 0.0)
                        wire.end_handle_offset_mm = (0.0, 0.0)
                    if wire.style in curve_styles:
                        wire_detail_wire_ids.add(wire.wire_id)
                        if previous_style not in curve_styles or abs(curve_mm - previous_curve) > 1e-9:
                            should_smooth_wire_chains = True
                    wire.twist_pitch_mm = twist_pitch
                    wire.pair_gap_mm = pair_gap
                    wire.signal_name = self.prop_signal_var.get().strip()
                    wire.from_connector = self.prop_from_connector_var.get().strip().upper()
                    wire.from_pin = self.prop_from_pin_var.get().strip()
                    wire.to_connector = self.prop_to_connector_var.get().strip().upper()
                    wire.to_pin = self.prop_to_pin_var.get().strip()
                    wire.cross_section_mm2 = cross_section
                    wire.length_mm = length_mm
                    wire.shielded = bool(self.prop_shielded_var.get())
                    wire.net_name = self.prop_net_var.get().strip()
                if apply_text:
                    wire.label = text_value.strip()
                touched_wires = True
            elif kind == "leader":
                leader = self._find_leader(ident)
                if not leader:
                    continue
                if color:
                    leader.color = color
                if apply_width:
                    leader.width_mm = max(0.2, width)
                if apply_scale:
                    leader.arrow_size_mm = max(0.5, scale_val)
                if apply_leader_detail:
                    leader.text_size_pt = max(4.0, leader_text_size)
                    leader.text_box = bool(leader_text_box)
                if apply_text:
                    leader.text = text_value.strip()
            elif kind == "dimension":
                dim = self._find_dimension(ident)
                if not dim:
                    continue
                if color:
                    dim.color = color
                if apply_width:
                    dim.line_width_mm = max(0.1, width)
                if apply_scale:
                    dim.arrow_size_mm = max(0.5, scale_val)
                if apply_dimension_detail:
                    dim.orientation = normalize_dimension_orientation(dim_orientation)
                    dim.offset_mm = dim_offset
                    dim.tolerance = dim_tolerance
                    dim.text_size_pt = max(4.0, leader_text_size)
                if apply_text:
                    dim.override_text = text_value.strip()
            elif kind == "connector":
                conn = self._find_connector(ident)
                if not conn:
                    continue
                if color:
                    conn.line_color = color
                if apply_width:
                    conn.line_width_mm = max(0.1, width)
                if apply_scale:
                    conn.scale = scale_val
                if apply_text:
                    conn.note = text_value.strip()
                if apply_connector_detail:
                    conn.part_number = self.prop_connector_part_var.get().strip()
                    conn.pin_count = max(1, int(try_float(self.prop_connector_pin_count_var.get(), 1)))
                    conn.pin_labels = parse_pin_labels(self.prop_connector_pin_labels_var.get())
                    conn.label_dx_mm = round(try_float(self.prop_connector_label_dx_var.get(), conn.label_dx_mm), 3)
                    conn.label_dy_mm = round(try_float(self.prop_connector_label_dy_var.get(), conn.label_dy_mm), 3)
                touched_connectors = True
            elif kind == "table":
                table = self._find_table(ident)
                if not table:
                    continue
                if color:
                    table.border_color = color
                if apply_width:
                    table.border_width_mm = max(0.1, width)
            elif kind == "text":
                note = self._find_text_note(ident)
                if not note:
                    continue
                if color:
                    note.color = color
                if apply_width:
                    note.font_size_pt = max(6.0, width)
                if apply_text:
                    note.text = text_value
            elif kind == "image":
                note = self._find_image_note(ident)
                if not note:
                    continue
                if apply_scale:
                    note.scale = scale_val
                    self._image_canvas_cache.pop(note.image_id, None)

        if should_smooth_wire_chains and len(wire_detail_wire_ids) > 1:
            self._smooth_wire_chains_for_style(wire_detail_wire_ids)
        if touched_wires:
            self._clear_wire_caches()
        if touched_connectors:
            self._clear_connector_caches()

    def _active_selected_leader_ids(self) -> List[str]:
        ids: List[str] = []
        for kind, ident in sorted(self._active_selected_items(), key=self._selection_sort_key):
            if kind == "leader" and self._find_leader(ident):
                ids.append(ident)
        return ids

    def prompt_selected_leader_text(self):
        leader_ids = self._active_selected_leader_ids()
        if not leader_ids:
            self.status("Geen leader geselecteerd.")
            return
        primary = self._find_leader(leader_ids[0])
        initial = primary.text if primary and len(leader_ids) == 1 else ""
        text = self._ask_string("Leader tekst:", initialvalue=initial)
        if text is None:
            return
        before = self._capture_before_change()
        for leader_id in leader_ids:
            leader = self._find_leader(leader_id)
            if leader:
                leader.text = text.strip()
        self.load_selection_properties_to_panel()
        self.redraw()
        self._commit_change(before, "Leader tekst gewijzigd.")

    def prompt_selected_leader_arrow_size(self):
        leader_ids = self._active_selected_leader_ids()
        if not leader_ids:
            self.status("Geen leader geselecteerd.")
            return
        primary = self._find_leader(leader_ids[0])
        initial = primary.arrow_size_mm if primary else self.default_leader_arrow_size_mm
        value = self._ask_float("Pijlgrootte mm:", initialvalue=initial, minvalue=0.5)
        if value is None:
            return
        before = self._capture_before_change()
        arrow_size = max(0.5, float(value))
        for leader_id in leader_ids:
            leader = self._find_leader(leader_id)
            if leader:
                leader.arrow_size_mm = arrow_size
        self.load_selection_properties_to_panel()
        self.redraw()
        self._commit_change(before, "Leader pijlgrootte gewijzigd.")

    def adjust_selected_leader_arrow_size(self, delta_mm: float):
        leader_ids = self._active_selected_leader_ids()
        if not leader_ids:
            self.status("Geen leader geselecteerd.")
            return
        before = self._capture_before_change()
        for leader_id in leader_ids:
            leader = self._find_leader(leader_id)
            if leader:
                leader.arrow_size_mm = max(0.5, leader.arrow_size_mm + delta_mm)
        self.load_selection_properties_to_panel()
        self.redraw()
        self._commit_change(before, "Leader pijlgrootte aangepast.")

    def _active_selected_dimension_ids(self) -> List[str]:
        ids: List[str] = []
        for kind, ident in sorted(self._active_selected_items(), key=self._selection_sort_key):
            if kind == "dimension" and self._find_dimension(ident):
                ids.append(ident)
        return ids

    def set_selected_dimension_orientation(self, orientation: str):
        dim_ids = self._active_selected_dimension_ids()
        if not dim_ids:
            self.status("Geen maatlijn geselecteerd.")
            return
        orientation = normalize_dimension_orientation(orientation)
        before = self._capture_before_change()
        for dim_id in dim_ids:
            dim = self._find_dimension(dim_id)
            if dim:
                dim.orientation = orientation
        self.load_selection_properties_to_panel()
        self.redraw()
        self._commit_change(before, f"Maatrichting: {dimension_orientation_label(orientation)}.")

    def adjust_selected_dimension_offset(self, delta_mm: float):
        dim_ids = self._active_selected_dimension_ids()
        if not dim_ids:
            self.status("Geen maatlijn geselecteerd.")
            return
        before = self._capture_before_change()
        for dim_id in dim_ids:
            dim = self._find_dimension(dim_id)
            if dim:
                dim.offset_mm = dim.offset_mm + delta_mm
        self.load_selection_properties_to_panel()
        self.redraw()
        self._commit_change(before, "Maatlijn offset aangepast.")

    def flip_selected_dimension_offset(self):
        dim_ids = self._active_selected_dimension_ids()
        if not dim_ids:
            self.status("Geen maatlijn geselecteerd.")
            return
        before = self._capture_before_change()
        for dim_id in dim_ids:
            dim = self._find_dimension(dim_id)
            if dim:
                dim.offset_mm = -dim.offset_mm
        self.load_selection_properties_to_panel()
        self.redraw()
        self._commit_change(before, "Maatlijn naar andere kant gezet.")

    def prompt_selected_dimension_tolerance(self):
        dim_ids = self._active_selected_dimension_ids()
        if not dim_ids:
            self.status("Geen maatlijn geselecteerd.")
            return
        first = self._find_dimension(dim_ids[0])
        value = self._ask_string("Tolerantie (bv. ±10 of +0.5/-0.2, leeg = geen):", initialvalue=first.tolerance if first else "")
        if value is None:
            return
        before = self._capture_before_change()
        for dim_id in dim_ids:
            dim = self._find_dimension(dim_id)
            if dim:
                dim.tolerance = value.strip()
        self.load_selection_properties_to_panel()
        self.redraw()
        self._commit_change(before, "Maatlijn tolerantie ingesteld.")

    def prompt_selected_dimension_value(self):
        dim_ids = self._active_selected_dimension_ids()
        if not dim_ids:
            self.status("Geen maatlijn geselecteerd.")
            return
        first = self._find_dimension(dim_ids[0])
        value = self._ask_string("Maatwaarde overschrijven (leeg = automatisch gemeten):", initialvalue=first.override_text if first else "")
        if value is None:
            return
        before = self._capture_before_change()
        for dim_id in dim_ids:
            dim = self._find_dimension(dim_id)
            if dim:
                dim.override_text = value.strip()
        self.load_selection_properties_to_panel()
        self.redraw()
        self._commit_change(before, "Maatwaarde aangepast.")

    def apply_defaults_from_panel(self):
        before = self._capture_before_change()
        color = self.prop_color_var.get().strip() or self.default_wire_color
        color_b = self.prop_color_b_var.get().strip() or self.default_wire_color_b
        width = max(0.2, try_float(self.prop_width_var.get(), self.default_wire_width_mm))
        wire_style = wire_style_internal(self.prop_wire_style_var.get().strip())
        curve_mm = try_float(self.prop_curve_var.get(), self.default_wire_curve_offset_mm)
        twist_pitch = max(1.0, try_float(self.prop_twist_pitch_var.get(), self.default_wire_twist_pitch_mm))
        pair_gap = max(0.2, try_float(self.prop_pair_gap_var.get(), self.default_wire_pair_gap_mm))
        selected_items = self._active_selected_items()
        arrow_size = self.default_leader_arrow_size_mm
        if any(kind == "leader" for kind, _ident in selected_items) or (not selected_items and self.mode == "draw_leader"):
            arrow_size = max(0.5, try_float(self.prop_scale_var.get(), self.default_leader_arrow_size_mm))
            self.default_leader_text_size_pt = max(4.0, try_float(self.prop_leader_text_size_var.get(), self.default_leader_text_size_pt))
            self.default_leader_text_box = bool(self.prop_leader_text_box_var.get())
        if any(kind == "dimension" for kind, _ident in selected_items) or (not selected_items and self.mode == "draw_dimension"):
            self.default_dimension_color = color
            self.default_dimension_line_width_mm = max(0.1, width)
            self.default_dimension_arrow_size_mm = max(0.5, try_float(self.prop_scale_var.get(), self.default_dimension_arrow_size_mm))
            self.default_dimension_text_size_pt = max(4.0, try_float(self.prop_leader_text_size_var.get(), self.default_dimension_text_size_pt))
            self.default_dimension_offset_mm = try_float(self.prop_dim_offset_var.get(), self.default_dimension_offset_mm)
            self.default_dimension_orientation = dimension_orientation_internal(self.prop_dim_orientation_var.get().strip())
            self.default_dimension_tolerance = self.prop_dim_tolerance_var.get().strip()
        self.default_wire_color = color
        self.default_wire_color_b = color_b
        self.default_wire_width_mm = width
        self.default_wire_style = wire_style
        self.default_wire_curve_offset_mm = curve_mm
        self.default_wire_twist_pitch_mm = twist_pitch
        self.default_wire_pair_gap_mm = pair_gap
        self.default_leader_color = color
        self.default_leader_width_mm = width
        self.default_leader_arrow_size_mm = arrow_size
        self.default_connector_line_color = color
        self.default_connector_line_width_mm = max(0.1, width)
        self.default_table_border_color = color
        self.default_table_border_width_mm = max(0.1, width)
        self._commit_change(before, "Default lijnstijl bijgewerkt voor nieuwe objecten.")

    # ---------------- Symbol import ----------------
    def import_step_symbol(self):
        path = self._ask_open_filename(
            title="Kies STEP connector file",
            filetypes=[("STEP", "*.step *.stp"), ("Alle bestanden", "*.*")],
            settings_key="last_import_dir",
        )
        if not path:
            return
        step_path = Path(path)
        try:
            geometry = parse_step_geometry(step_path)
            if not geometry.polylines:
                raise ValueError("Geen bruikbare 3D wireframe data gevonden in STEP.")

            self._prepare_dialog_parent()
            dialog = StepPreviewDialog(self, default_name=safe_name(step_path.stem, "symbol"), geometry=geometry)
            if not dialog.result_data:
                return
            symbol_name = dialog.result_data["name"]
            projection = dialog.result_data["projection"]

            polylines, w, h = project_step_geometry(geometry, projection=projection)
            if w < 0.001 or h < 0.001:
                raise ValueError("2D contour kon niet uit STEP worden gehaald.")
            name = symbol_name
            suffix = 1
            base = name
            while name in self.symbols:
                suffix += 1
                name = f"{base}_{suffix}"
            before = self._capture_before_change()
            self.symbols[name] = StepSymbol(
                name=name,
                source_path=str(step_path),
                projection=projection,
                polylines=polylines,
                width_mm=w,
                height_mm=h,
            )
            self.active_symbol = name
            self._persist_symbol_to_library(self.symbols[name])
            self._refresh_symbol_list(select_name=name)
            backend = geometry.backend or "regex"
            fallback_note = f"; fallback: {geometry.warning}" if geometry.warning and backend == "regex" else ""
            self.status(f"STEP geïmporteerd via {backend} ({projection}): {step_path.name}{fallback_note}")
            self.set_mode("place_connector")
            self._commit_change(before)
        except Exception as exc:
            self._show_error(
                "STEP importeren mislukt.\n\n"
                "Tip: gebruik de 3D preview om eerst een logische projectiezijde te kiezen.\n"
                f"Fout: {exc}"
            )

    def _refresh_symbol_list(self, select_name: Optional[str] = None):
        current = select_name or self.active_symbol
        self.symbol_list.delete(0, tk.END)
        names = sorted(self.symbols.keys())
        for name in names:
            sym = self.symbols[name]
            self.symbol_list.insert(tk.END, f"{name} [{sym.projection}] ({sym.width_mm:.1f} x {sym.height_mm:.1f} mm)")
        if current and current in names:
            idx = names.index(current)
            self.symbol_list.select_clear(0, tk.END)
            self.symbol_list.select_set(idx)
            self.symbol_list.see(idx)
            self.active_symbol = current
        elif names:
            self.symbol_list.select_set(0)
            self.active_symbol = names[0]
        else:
            self.active_symbol = None

    def _on_symbol_select(self, _event=None):
        sel = self.symbol_list.curselection()
        if not sel:
            return
        names = sorted(self.symbols.keys())
        idx = sel[0]
        if idx < 0 or idx >= len(names):
            return
        self.active_symbol = names[idx]
        if self.mode == "place_connector":
            self.status(f"Actieve connector: {self.active_symbol}")

    def _on_symbol_activate(self, _event=None):
        """Dubbelklik/Enter op een bibliotheeksymbool: start direct de plaatsmodus."""
        self._on_symbol_select()
        self._place_selected_symbol()
        return "break"

    def _symbol_name_at_list_index(self, index: int) -> Optional[str]:
        names = sorted(self.symbols.keys())
        if 0 <= index < len(names):
            return names[index]
        return None

    def _show_symbol_context_menu(self, event):
        index = self.symbol_list.nearest(event.y)
        name = self._symbol_name_at_list_index(index)
        if not name:
            return
        self.symbol_list.select_clear(0, tk.END)
        self.symbol_list.select_set(index)
        self._on_symbol_select()
        menu = tk.Menu(self, tearoff=0)
        menu.add_command(label=f"Plaats '{name}'", command=self._place_selected_symbol)
        menu.add_separator()
        menu.add_command(label="Verwijder uit bibliotheek", command=lambda n=name: self._remove_symbol_from_library(n))
        try:
            menu.tk_popup(event.x_root, event.y_root)
        finally:
            menu.grab_release()

    def _remove_symbol_from_library(self, name: str):
        if name not in self.symbols:
            return
        in_use = any(c.symbol_name == name for c in self.connectors)
        if not messagebox.askyesno(
            APP_TITLE,
            f"'{name}' uit de bibliotheek verwijderen?"
            + ("\n\nDe connector wordt op deze tekening nog gebruikt en blijft daar staan,\n"
               "maar verschijnt niet meer in de bibliotheek bij een volgende sessie." if in_use else ""),
            parent=self,
        ):
            return
        stored = [s for s in self._load_symbols_library() if s.get("name") != name]
        try:
            self._save_symbols_library(stored)
        except OSError:
            pass
        if in_use:
            self.status(f"'{name}' uit bibliotheek verwijderd (blijft in gebruik op de tekening).")
            return
        self.symbols.pop(name, None)
        if self.active_symbol == name:
            self.active_symbol = None
        self._refresh_symbol_list()
        self.status(f"Connector '{name}' uit bibliotheek verwijderd.")

    def _place_selected_symbol(self):
        """Activeer de plaatsmodus voor de in de bibliotheek geselecteerde connector."""
        if not self.symbols:
            self._show_info("Importeer eerst een STEP connector via 'STEP import'.")
            return
        if not self.active_symbol or self.active_symbol not in self.symbols:
            sel = self.symbol_list.curselection()
            names = sorted(self.symbols.keys())
            if sel and 0 <= sel[0] < len(names):
                self.active_symbol = names[sel[0]]
            elif names:
                self.active_symbol = names[0]
        if not self.active_symbol or self.active_symbol not in self.symbols:
            self._show_info("Selecteer eerst een connector in de bibliotheek.")
            return
        self.set_mode("place_connector")
        self.status(f"Klik op de tekening om '{self.active_symbol}' te plaatsen.")

    # ---------------- Coordinate transforms ----------------
    def world_to_canvas(self, x_mm: float, y_mm: float) -> Tuple[float, float]:
        return x_mm * self.zoom + self.pan_x, y_mm * self.zoom + self.pan_y

    def canvas_to_world(self, x_px: float, y_px: float) -> Tuple[float, float]:
        return (x_px - self.pan_x) / self.zoom, (y_px - self.pan_y) / self.zoom

    def zoom_by(self, factor: float):
        old_zoom = self.zoom
        cx = self.canvas.winfo_width() / 2.0
        cy = self.canvas.winfo_height() / 2.0
        wx, wy = self.canvas_to_world(cx, cy)
        self.zoom = clamp(self.zoom * factor, 0.3, 8.0)
        if abs(self.zoom - old_zoom) < 1e-9:
            return
        self.pan_x = cx - wx * self.zoom
        self.pan_y = cy - wy * self.zoom
        self._redraw_zoom_preview()

    def _redraw_zoom_preview(self):
        """Toon direct een gecachete zoom-preview en render na 120 ms scherp."""

        self._aa_zoom_preview = True
        if self._aa_zoom_settle_after_id is not None:
            try:
                self.after_cancel(self._aa_zoom_settle_after_id)
            except tk.TclError:
                pass
        self.redraw()
        self._aa_zoom_settle_after_id = self.after(120, self._finish_aa_zoom_settle)

    def _finish_aa_zoom_settle(self):
        self._aa_zoom_settle_after_id = None
        self._aa_zoom_preview = False
        if self.canvas.winfo_exists():
            self.redraw()

    def _is_shift_pressed(self, event) -> bool:
        return bool(event.state & 0x0001)

    def _is_ctrl_pressed(self, event) -> bool:
        return bool(event.state & 0x0004)

    def _has_selection_modifier(self, event) -> bool:
        return self._is_shift_pressed(event) or self._is_ctrl_pressed(event)

    def _orthogonal_snap(self, point: Tuple[float, float], anchor: Tuple[float, float]) -> Tuple[float, float]:
        dx = point[0] - anchor[0]
        dy = point[1] - anchor[1]
        if abs(dx) >= abs(dy):
            return (point[0], anchor[1])
        return (anchor[0], point[1])

    def _wire_endpoint_drag_anchor(self, wire: Optional[WirePath], endpoint_index: int) -> Optional[Tuple[float, float]]:
        if not wire or len(wire.points_mm) < 2:
            return None
        return wire.points_mm[-1] if endpoint_index <= 0 else wire.points_mm[0]

    def _wire_endpoint_drag_constrained_point(
        self,
        point: Tuple[float, float],
        event,
        wire: Optional[WirePath],
        endpoint_index: int,
    ) -> Tuple[Tuple[float, float], bool]:
        anchor = self._wire_endpoint_drag_anchor(wire, endpoint_index)
        if anchor is None or event is None or not self._is_shift_pressed(event):
            return (point, False)
        return (self._orthogonal_snap(point, anchor), True)

    def _wire_snap_if_needed(
        self,
        point: Tuple[float, float],
        event,
        exclude_wire_ids: Optional[set[str]] = None,
        exclude_wire_endpoints: Optional[set[Tuple[str, int]]] = None,
    ) -> Tuple[Tuple[float, float], Optional[dict]]:
        snapped = self._snap_world(point, event, exclude_wire_endpoints=exclude_wire_endpoints)
        snap_meta = None
        if self.snap_endpoint_enabled_var.get():
            snap_meta = self._wire_segment_snap_info(snapped, exclude_wire_ids=exclude_wire_ids)
            if snap_meta:
                snapped = snap_meta["point"]
        return (snapped, snap_meta)

    def _wire_segment_snap_info(self, point: Tuple[float, float], exclude_wire_ids: Optional[set[str]] = None) -> Optional[dict]:
        tol_mm = max(1.0, 6.0 / self.zoom)
        self._ensure_wire_segment_indexes()
        best_meta = None
        best_dist = float("inf")
        for entry in self._query_segment_index(self._wire_snap_segment_index, point, tol_mm):
            if exclude_wire_ids and entry["wire_id"] in exclude_wire_ids:
                continue
            nearest, local_t, dist = closest_point_on_segment(point[0], point[1], entry["a"][0], entry["a"][1], entry["b"][0], entry["b"][1])
            if dist > tol_mm or dist >= best_dist:
                continue
            param_t = entry["param_base"] + entry["param_step"] * local_t
            if param_t <= 0.03 or param_t >= 0.97:
                continue
            best_dist = dist
            best_meta = {
                "kind": "wire_segment",
                "wire_id": entry["wire_id"],
                "point": nearest,
                "param_t": param_t,
            }
        return best_meta

    def _wire_endpoints(self, wire: WirePath) -> Optional[Tuple[Tuple[float, float], Tuple[float, float]]]:
        if len(wire.points_mm) < 2:
            return None
        return (wire.points_mm[0], wire.points_mm[-1])

    def _wire_geometry_signature(self, wire: WirePath) -> Tuple:
        return (
            wire.wire_id,
            normalize_wire_style(wire.style),
            round(wire.curve_offset_mm, 4),
            round(wire.start_handle_offset_mm[0], 4),
            round(wire.start_handle_offset_mm[1], 4),
            round(wire.end_handle_offset_mm[0], 4),
            round(wire.end_handle_offset_mm[1], 4),
            round(wire.twist_pitch_mm, 4),
            round(wire.pair_gap_mm, 4),
            tuple((round(x, 4), round(y, 4)) for x, y in wire.points_mm),
        )

    def _wire_connectivity_signature_value(self) -> Tuple:
        items = []
        for wire in self.wires:
            endpoints = self._wire_endpoints(wire)
            if endpoints is None:
                continue
            items.append(
                (
                    wire.wire_id,
                    round(endpoints[0][0], 4),
                    round(endpoints[0][1], 4),
                    round(endpoints[1][0], 4),
                    round(endpoints[1][1], 4),
                )
            )
        return tuple(items)

    def _wire_connectivity_graph(self) -> Dict[str, set[str]]:
        signature = self._wire_connectivity_signature_value()
        if signature == self._wire_connectivity_signature:
            return self._wire_connectivity_cache

        graph: Dict[str, set[str]] = {wire.wire_id: set() for wire in self.wires}
        endpoint_map: Dict[Tuple[int, int], List[str]] = {}
        tol = 0.05
        for wire in self.wires:
            endpoints = self._wire_endpoints(wire)
            if endpoints is None:
                continue
            for point in endpoints:
                key = (round(point[0] / tol), round(point[1] / tol))
                endpoint_map.setdefault(key, []).append(wire.wire_id)

        for ids in endpoint_map.values():
            if len(ids) < 2:
                continue
            unique_ids = list(dict.fromkeys(ids))
            for wire_id in unique_ids:
                graph.setdefault(wire_id, set()).update(other for other in unique_ids if other != wire_id)

        self._wire_connectivity_signature = signature
        self._wire_connectivity_cache = graph
        return graph

    def _wire_segment_index_signature_value(self) -> Tuple:
        return tuple(self._wire_geometry_signature(wire) for wire in self.wires)

    def _segment_index_cell_keys(
        self, a: Tuple[float, float], b: Tuple[float, float], cell_size_mm: float = 10.0
    ) -> List[Tuple[int, int]]:
        min_x = min(a[0], b[0])
        max_x = max(a[0], b[0])
        min_y = min(a[1], b[1])
        max_y = max(a[1], b[1])
        x0 = math.floor(min_x / cell_size_mm)
        x1 = math.floor(max_x / cell_size_mm)
        y0 = math.floor(min_y / cell_size_mm)
        y1 = math.floor(max_y / cell_size_mm)
        keys: List[Tuple[int, int]] = []
        for cx in range(x0, x1 + 1):
            for cy in range(y0, y1 + 1):
                keys.append((cx, cy))
        return keys

    def _ensure_wire_segment_indexes(self):
        signature = self._wire_segment_index_signature_value()
        if signature == self._wire_segment_index_signature:
            return

        hit_index: Dict[Tuple[int, int], List[dict]] = {}
        snap_index: Dict[Tuple[int, int], List[dict]] = {}
        for wire_order, wire in enumerate(self.wires):
            display_polylines = self._wire_display_polylines(wire)
            for poly_idx, line in enumerate(display_polylines):
                for seg_idx in range(len(line) - 1):
                    a = line[seg_idx]
                    b = line[seg_idx + 1]
                    if math.dist(a, b) < 1e-9:
                        continue
                    entry = {
                        "id": ("hit", wire.wire_id, poly_idx, seg_idx),
                        "wire_id": wire.wire_id,
                        "wire_order": wire_order,
                        "a": a,
                        "b": b,
                        "extra_tol": (abs(wire.pair_gap_mm) / 2.0)
                        if normalize_wire_style(wire.style) in {"twisted_pair", "twisted_pair_curve"}
                        else 0.0,
                    }
                    for key in self._segment_index_cell_keys(a, b):
                        hit_index.setdefault(key, []).append(entry)

            centerline = self._wire_centerline_points(wire, curve_samples=40)
            seg_count = max(1, len(centerline) - 1)
            for seg_idx in range(len(centerline) - 1):
                a = centerline[seg_idx]
                b = centerline[seg_idx + 1]
                if math.dist(a, b) < 1e-9:
                    continue
                entry = {
                    "id": ("snap", wire.wire_id, seg_idx),
                    "wire_id": wire.wire_id,
                    "wire_order": wire_order,
                    "a": a,
                    "b": b,
                    "param_base": seg_idx / seg_count,
                    "param_step": 1.0 / seg_count,
                }
                for key in self._segment_index_cell_keys(a, b):
                    snap_index.setdefault(key, []).append(entry)

        self._wire_segment_index_signature = signature
        self._wire_hit_segment_index = hit_index
        self._wire_snap_segment_index = snap_index

    def _query_segment_index(self, index: Dict[Tuple[int, int], List[dict]], point: Tuple[float, float], tol_mm: float) -> List[dict]:
        cell_size_mm = 10.0
        x0 = math.floor((point[0] - tol_mm) / cell_size_mm)
        x1 = math.floor((point[0] + tol_mm) / cell_size_mm)
        y0 = math.floor((point[1] - tol_mm) / cell_size_mm)
        y1 = math.floor((point[1] + tol_mm) / cell_size_mm)
        seen = set()
        items: List[dict] = []
        for cx in range(x0, x1 + 1):
            for cy in range(y0, y1 + 1):
                for entry in index.get((cx, cy), []):
                    if entry["id"] in seen:
                        continue
                    seen.add(entry["id"])
                    items.append(entry)
        return items

    def _connected_wire_ids(self, wire_id: str) -> List[str]:
        graph = self._wire_connectivity_graph()
        if wire_id not in graph:
            return [wire_id]
        visited: List[str] = []
        stack = [wire_id]
        seen = set()
        while stack:
            current = stack.pop()
            if current in seen:
                continue
            seen.add(current)
            visited.append(current)
            for other in sorted(graph.get(current, []), reverse=True):
                if other not in seen:
                    stack.append(other)
        return sorted(visited)

    def _selection_sort_key(self, item: Tuple[str, str]) -> Tuple[int, str]:
        order = {"connector": 0, "wire": 1, "leader": 2, "table": 3, "text": 4, "image": 5}
        return (order.get(item[0], 99), item[1])

    def _item_exists(self, kind: str, ident: str) -> bool:
        if kind == "connector":
            return self._find_connector(ident) is not None
        if kind == "wire":
            return self._find_wire(ident) is not None
        if kind == "leader":
            return self._find_leader(ident) is not None
        if kind == "dimension":
            return self._find_dimension(ident) is not None
        if kind == "table":
            return self._find_table(ident) is not None
        if kind == "text":
            return self._find_text_note(ident) is not None
        if kind == "image":
            return self._find_image_note(ident) is not None
        return False

    def _leader_elbow_point(self, leader: Leader) -> Tuple[float, float]:
        return (leader.start_mm[0] + (leader.end_mm[0] - leader.start_mm[0]) * 0.45, leader.start_mm[1])

    def _leader_polyline(self, leader: Leader) -> List[Tuple[float, float]]:
        return [leader.start_mm, self._leader_elbow_point(leader), leader.end_mm]

    def _leader_arrow_points(self, leader: Leader) -> List[Tuple[float, float]]:
        tip = leader.start_mm
        path = self._leader_polyline(leader)
        next_point = path[1] if math.dist(tip, path[1]) > 1e-6 else leader.end_mm
        dx = tip[0] - next_point[0]
        dy = tip[1] - next_point[1]
        length = math.hypot(dx, dy)
        if length <= 1e-6:
            dx, dy, length = -1.0, 0.0, 1.0
        ux = dx / length
        uy = dy / length
        size = max(0.5, float(getattr(leader, "arrow_size_mm", DEFAULT_LEADER_ARROW_SIZE_MM)))
        half_width = size * 0.42
        base = (tip[0] - ux * size, tip[1] - uy * size)
        px = -uy * half_width
        py = ux * half_width
        return [tip, (base[0] + px, base[1] + py), (base[0] - px, base[1] - py)]

    def _leader_text_bbox(self, leader: Leader, include_empty: bool = False) -> Optional[Tuple[float, float, float, float]]:
        text = str(leader.text or "").replace("\r\n", "\n")
        if not text.strip() and not include_empty:
            return None
        lines = text.split("\n") if text else [""]
        font_mm = max(1.4, float(getattr(leader, "text_size_pt", DEFAULT_LEADER_TEXT_SIZE_PT)) * 0.352778)
        max_chars = max(1, max(len(line) for line in lines))
        width_mm = max(14.0, font_mm * 0.58 * max_chars + 4.0)
        height_mm = max(5.6, len(lines) * font_mm * 1.18 + 2.4)
        sx, _sy = leader.start_mm
        ex, ey = leader.end_mm
        side = 1.0 if ex >= sx else -1.0
        if side > 0:
            x1 = ex + 2.0
        else:
            x1 = ex - 2.0 - width_mm
        y1 = ey - height_mm - 1.2
        return (x1, y1, x1 + width_mm, y1 + height_mm)

    def _leader_world_bbox(self, leader: Leader) -> Tuple[float, float, float, float]:
        boxes = [polyline_bbox([self._leader_polyline(leader)]), polyline_bbox([self._leader_arrow_points(leader)])]
        label_box = self._leader_text_bbox(leader)
        if label_box:
            boxes.append(label_box)
        return (
            min(box[0] for box in boxes),
            min(box[1] for box in boxes),
            max(box[2] for box in boxes),
            max(box[3] for box in boxes),
        )

    @staticmethod
    def _arrow_triangle(tip: Tuple[float, float], point_dir: Tuple[float, float], size: float) -> List[Tuple[float, float]]:
        ux, uy = point_dir
        length = math.hypot(ux, uy)
        if length <= 1e-9:
            ux, uy, length = 1.0, 0.0, 1.0
        ux, uy = ux / length, uy / length
        half = size * 0.4
        base = (tip[0] - ux * size, tip[1] - uy * size)
        px, py = (-uy * half, ux * half)
        return [tip, (base[0] + px, base[1] + py), (base[0] - px, base[1] - py)]

    def _dimension_value_text(self, dim: DimensionLine, value: float) -> str:
        override = str(getattr(dim, "override_text", "") or "").strip()
        if override:
            base = override
        else:
            dec = max(0, int(getattr(dim, "decimals", 0) or 0))
            base = f"{value:.{dec}f}"
        tol = str(getattr(dim, "tolerance", "") or "").strip()
        return f"{base} {tol}".strip() if tol else base

    def _dimension_geometry(self, dim: DimensionLine) -> dict:
        p1 = (float(dim.p1_mm[0]), float(dim.p1_mm[1]))
        p2 = (float(dim.p2_mm[0]), float(dim.p2_mm[1]))
        orient = normalize_dimension_orientation(getattr(dim, "orientation", "horizontal"))
        if orient == "horizontal":
            d = (1.0, 0.0)
            n = (0.0, -1.0)
        elif orient == "vertical":
            d = (0.0, 1.0)
            n = (-1.0, 0.0)
        else:  # aligned
            vx, vy = (p2[0] - p1[0], p2[1] - p1[1])
            ln = math.hypot(vx, vy) or 1.0
            d = (vx / ln, vy / ln)
            n = (-d[1], d[0])
        offset = float(getattr(dim, "offset_mm", DEFAULT_DIMENSION_OFFSET_MM))

        def dot(p, v):
            return p[0] * v[0] + p[1] * v[1]

        base = max(dot(p1, n), dot(p2, n))
        level = base + offset

        def foot(p):
            shift = level - dot(p, n)
            return (p[0] + n[0] * shift, p[1] + n[1] * shift)

        f1 = foot(p1)
        f2 = foot(p2)
        value = abs(dot((p2[0] - p1[0], p2[1] - p1[1]), d))

        gap = 1.2
        overshoot = 1.6

        def ext(p, f):
            start = (p[0] + n[0] * gap, p[1] + n[1] * gap)
            end = (f[0] + n[0] * overshoot, f[1] + n[1] * overshoot)
            return (start, end)

        ext1 = ext(p1, f1)
        ext2 = ext(p2, f2)

        ux, uy = (f2[0] - f1[0], f2[1] - f1[1])
        ul = math.hypot(ux, uy)
        u = (ux / ul, uy / ul) if ul > 1e-6 else d
        arrow_size = max(0.5, float(getattr(dim, "arrow_size_mm", DEFAULT_DIMENSION_ARROW_SIZE_MM)))
        arrow1 = self._arrow_triangle(f1, (-u[0], -u[1]), arrow_size)
        arrow2 = self._arrow_triangle(f2, u, arrow_size)

        mid = ((f1[0] + f2[0]) / 2.0, (f1[1] + f2[1]) / 2.0)
        font_mm = max(1.4, float(getattr(dim, "text_size_pt", DEFAULT_DIMENSION_TEXT_SIZE_PT)) * 0.352778)
        text_pos = (mid[0] + n[0] * (font_mm * 0.95), mid[1] + n[1] * (font_mm * 0.95))
        return {
            "feet": (f1, f2),
            "ext": (ext1, ext2),
            "arrows": (arrow1, arrow2),
            "u": u,
            "n": n,
            "mid": mid,
            "text_pos": text_pos,
            "value": value,
            "text": self._dimension_value_text(dim, value),
            "font_mm": font_mm,
        }

    def _dimension_world_bbox(self, dim: DimensionLine) -> Tuple[float, float, float, float]:
        geo = self._dimension_geometry(dim)
        pts: List[Tuple[float, float]] = [dim.p1_mm, dim.p2_mm, geo["feet"][0], geo["feet"][1], geo["text_pos"]]
        for seg in geo["ext"]:
            pts.extend(seg)
        for tri in geo["arrows"]:
            pts.extend(tri)
        half = max(6.0, geo["font_mm"] * len(geo["text"]) * 0.32)
        pts.append((geo["text_pos"][0] - half, geo["text_pos"][1] - geo["font_mm"]))
        pts.append((geo["text_pos"][0] + half, geo["text_pos"][1] + geo["font_mm"]))
        return (
            min(p[0] for p in pts),
            min(p[1] for p in pts),
            max(p[0] for p in pts),
            max(p[1] for p in pts),
        )

    def _item_bbox(self, kind: str, ident: str) -> Optional[Tuple[float, float, float, float]]:
        if kind == "connector":
            obj = self._find_connector(ident)
            return self._connector_world_bbox(obj) if obj else None
        if kind == "wire":
            obj = self._find_wire(ident)
            if not obj:
                return None
            x1, y1, x2, y2 = polyline_bbox(self._wire_display_polylines(obj))
            pad = max(0.4, obj.width_mm * 0.5)
            return (x1 - pad, y1 - pad, x2 + pad, y2 + pad)
        if kind == "leader":
            obj = self._find_leader(ident)
            return self._leader_world_bbox(obj) if obj else None
        if kind == "dimension":
            obj = self._find_dimension(ident)
            return self._dimension_world_bbox(obj) if obj else None
        if kind == "table":
            obj = self._find_table(ident)
            if not obj:
                return None
            tw, th = self._table_size(obj)
            return (obj.x_mm, obj.y_mm, obj.x_mm + tw, obj.y_mm + th)
        if kind == "text":
            obj = self._find_text_note(ident)
            return self._text_note_bbox(obj) if obj else None
        if kind == "image":
            obj = self._find_image_note(ident)
            return self._image_note_bbox(obj) if obj else None
        return None

    def _valid_selected_items(self) -> set[Tuple[str, str]]:
        if not self.selected_items:
            return set()
        valid = {item for item in self.selected_items if self._item_exists(item[0], item[1])}
        if len(valid) != len(self.selected_items):
            self.selected_items = set(valid)
        if not valid:
            self.selected = None
            self.selected_wire_ids.clear()
        elif self.selected not in valid:
            self.selected = sorted(valid, key=self._selection_sort_key)[0]
        return valid

    def _active_selected_items(self) -> set[Tuple[str, str]]:
        valid = self._valid_selected_items()
        if valid:
            return valid
        if self.selected and self._item_exists(self.selected[0], self.selected[1]):
            return {self.selected}
        return set()

    def _set_single_selection(self, kind: str, ident: str):
        if kind == "wire":
            self._set_primary_wire_selection(ident)
            return
        if not self._item_exists(kind, ident):
            self._clear_selection()
            return
        self.selected = (kind, ident)
        self.selected_items.clear()
        self.selected_wire_ids.clear()

    def _set_selected_items(self, items: set[Tuple[str, str]] | List[Tuple[str, str]], primary: Optional[Tuple[str, str]] = None):
        valid = {item for item in items if self._item_exists(item[0], item[1])}
        if not valid:
            self._clear_selection()
            return
        self.selected_items = set(valid)
        chosen = primary if primary in valid else sorted(valid, key=self._selection_sort_key)[0]
        self.selected = chosen
        wire_ids = {ident for kind, ident in valid if kind == "wire"}
        self.selected_wire_ids = set(wire_ids)

    def _selection_count_for_kind(self, kind: str) -> int:
        items = self._active_selected_items()
        if len(items) <= 1:
            return 1
        return sum(1 for item_kind, _ident in items if item_kind == kind)

    def _is_item_selected(self, kind: str, ident: str) -> bool:
        if self.selected_items:
            return (kind, ident) in self.selected_items
        return self.selected == (kind, ident)

    def _selected_wire_id_set_for_drawing(self) -> set[str]:
        if self.selected_items:
            return {ident for kind, ident in self.selected_items if kind == "wire"}
        if self.selected and self.selected[0] == "wire":
            return set(self._selected_wire_ids())
        return set()

    def _normalized_box(self, a: Tuple[float, float], b: Tuple[float, float]) -> Tuple[float, float, float, float]:
        return (min(a[0], b[0]), min(a[1], b[1]), max(a[0], b[0]), max(a[1], b[1]))

    def _bbox_intersects(self, a: Tuple[float, float, float, float], b: Tuple[float, float, float, float]) -> bool:
        return a[0] <= b[2] and a[2] >= b[0] and a[1] <= b[3] and a[3] >= b[1]

    def _items_in_box(self, box: Tuple[float, float, float, float]) -> set[Tuple[str, str]]:
        found: set[Tuple[str, str]] = set()
        for note in self.image_notes:
            item = ("image", note.image_id)
            bbox = self._item_bbox(*item)
            if bbox and self._bbox_intersects(bbox, box):
                found.add(item)
        for conn in self.connectors:
            item = ("connector", conn.connector_id)
            bbox = self._item_bbox(*item)
            if bbox and self._bbox_intersects(bbox, box):
                found.add(item)
        for wire in self.wires:
            item = ("wire", wire.wire_id)
            bbox = self._item_bbox(*item)
            if bbox and self._bbox_intersects(bbox, box):
                found.add(item)
        for leader in self.leaders:
            item = ("leader", leader.leader_id)
            bbox = self._item_bbox(*item)
            if bbox and self._bbox_intersects(bbox, box):
                found.add(item)
        for table in self.tables:
            item = ("table", table.table_id)
            bbox = self._item_bbox(*item)
            if bbox and self._bbox_intersects(bbox, box):
                found.add(item)
        for note in self.text_notes:
            item = ("text", note.note_id)
            bbox = self._item_bbox(*item)
            if bbox and self._bbox_intersects(bbox, box):
                found.add(item)
        return found

    def _box_select_mode_from_event(self, event) -> str:
        if self._is_ctrl_pressed(event):
            return "toggle"
        if self._is_shift_pressed(event):
            return "add"
        return "replace"

    def _begin_box_select(self, point: Tuple[float, float], event):
        mode = self._box_select_mode_from_event(event)
        base_items = self._active_selected_items() if mode in {"add", "toggle"} else set()
        if mode == "replace":
            self._clear_selection()
            self.load_selection_properties_to_panel()
        self.box_select_state = {
            "start": point,
            "current": point,
            "mode": mode,
            "base_items": set(base_items),
        }
        self.drag_start_world = None
        self.drag_original_world = None
        self.drag_original_points = None
        self.drag_original_wire_points = None
        self.drag_original_items = None
        self.leader_endpoint_drag_state = None
        self.wire_endpoint_drag_state = None
        self.wire_tangent_drag_state = None
        self.wire_curve_drag_state = None
        self.status("Sleep een kader om objecten te selecteren.")
        self.redraw()

    def _finish_box_select(self):
        state = self.box_select_state
        if not state:
            return
        start = state.get("start", (0.0, 0.0))
        current = state.get("current", start)
        box = self._normalized_box(start, current)
        box_items = self._items_in_box(box)
        base_items = set(state.get("base_items", set()))
        mode = state.get("mode", "replace")
        if mode == "add":
            new_items = base_items | box_items
        elif mode == "toggle":
            new_items = base_items ^ box_items
        else:
            new_items = box_items
        primary = sorted(box_items, key=self._selection_sort_key)[0] if box_items else None
        self.box_select_state = None
        self._set_selected_items(new_items, primary=primary)
        self.load_selection_properties_to_panel()
        self.redraw()
        count = len(self._active_selected_items())
        self.status(f"{count} object(en) geselecteerd." if count else "Geen objecten geselecteerd.")

    def _select_item_with_modifier(self, item: Tuple[str, str], event) -> bool:
        if not self._has_selection_modifier(event):
            return False
        if item[0] == "wire":
            return self._select_wire_chain_with_modifier(item[1], event)
        current = self._active_selected_items()
        if self._is_ctrl_pressed(event):
            if item in current:
                current.remove(item)
            else:
                current.add(item)
        else:
            current.add(item)
        self._set_selected_items(current, primary=item if item in current else None)
        self.load_selection_properties_to_panel()
        self.redraw()
        count = len(self._active_selected_items())
        self.status(f"{count} object(en) geselecteerd." if count else "Selectie leeg.")
        return True

    def _capture_drag_original_items(self, items: set[Tuple[str, str]]) -> Dict[Tuple[str, str], object]:
        originals: Dict[Tuple[str, str], object] = {}
        for kind, ident in items:
            if kind == "connector":
                obj = self._find_connector(ident)
                if obj:
                    originals[(kind, ident)] = (obj.x_mm, obj.y_mm)
            elif kind == "wire":
                obj = self._find_wire(ident)
                if obj:
                    originals[(kind, ident)] = list(obj.points_mm)
            elif kind == "leader":
                obj = self._find_leader(ident)
                if obj:
                    originals[(kind, ident)] = (obj.start_mm, obj.end_mm)
            elif kind == "dimension":
                obj = self._find_dimension(ident)
                if obj:
                    originals[(kind, ident)] = (obj.p1_mm, obj.p2_mm)
            elif kind == "table":
                obj = self._find_table(ident)
                if obj:
                    originals[(kind, ident)] = (obj.x_mm, obj.y_mm)
            elif kind == "text":
                obj = self._find_text_note(ident)
                if obj:
                    originals[(kind, ident)] = (obj.x_mm, obj.y_mm)
            elif kind == "image":
                obj = self._find_image_note(ident)
                if obj:
                    originals[(kind, ident)] = (obj.x_mm, obj.y_mm)
        return originals

    def _apply_drag_original_items(self, originals: Dict[Tuple[str, str], object], dx: float, dy: float) -> bool:
        moved = False
        for (kind, ident), original in originals.items():
            if kind == "connector":
                obj = self._find_connector(ident)
                if obj:
                    obj.x_mm = original[0] + dx
                    obj.y_mm = original[1] + dy
                    moved = True
            elif kind == "wire":
                obj = self._find_wire(ident)
                if obj:
                    obj.points_mm = [(px + dx, py + dy) for px, py in original]
                    moved = True
            elif kind == "leader":
                obj = self._find_leader(ident)
                if obj:
                    obj.start_mm = (original[0][0] + dx, original[0][1] + dy)
                    obj.end_mm = (original[1][0] + dx, original[1][1] + dy)
                    moved = True
            elif kind == "dimension":
                obj = self._find_dimension(ident)
                if obj:
                    obj.p1_mm = (original[0][0] + dx, original[0][1] + dy)
                    obj.p2_mm = (original[1][0] + dx, original[1][1] + dy)
                    moved = True
            elif kind == "table":
                obj = self._find_table(ident)
                if obj:
                    obj.x_mm = original[0] + dx
                    obj.y_mm = original[1] + dy
                    moved = True
            elif kind == "text":
                obj = self._find_text_note(ident)
                if obj:
                    obj.x_mm = original[0] + dx
                    obj.y_mm = original[1] + dy
                    moved = True
            elif kind == "image":
                obj = self._find_image_note(ident)
                if obj:
                    obj.x_mm = original[0] + dx
                    obj.y_mm = original[1] + dy
                    moved = True
        return moved

    def _wire_ids_for_selection(self, wire_id: str, *, chain: bool = True) -> List[str]:
        return self._connected_wire_ids(wire_id) if chain else [wire_id]

    def _valid_selected_wire_ids(self) -> set[str]:
        has_wire_selection = bool(self.selected and self.selected[0] == "wire") or any(
            kind == "wire" for kind, _ident in self.selected_items
        )
        if not has_wire_selection:
            self.selected_wire_ids.clear()
            return set()
        existing = {wire.wire_id for wire in self.wires}
        valid = {wire_id for wire_id in self.selected_wire_ids if wire_id in existing}
        if len(valid) != len(self.selected_wire_ids):
            self.selected_wire_ids = set(valid)
        return valid

    def _set_wire_selection(self, primary_wire_id: str, wire_ids: List[str] | set[str]):
        valid = {wire.wire_id for wire in self.wires}
        selected_ids = {wire_id for wire_id in wire_ids if wire_id in valid}
        if primary_wire_id not in selected_ids and primary_wire_id in valid:
            selected_ids.add(primary_wire_id)
        if not selected_ids:
            self.selected = None
            self.selected_items.clear()
            self.selected_wire_ids.clear()
            return
        self.selected = ("wire", primary_wire_id if primary_wire_id in selected_ids else sorted(selected_ids)[0])
        self.selected_wire_ids = set(selected_ids)
        self.selected_items = {("wire", wire_id) for wire_id in selected_ids}

    def _set_primary_wire_selection(self, wire_id: str):
        self.selected = ("wire", wire_id)
        self.selected_items.clear()
        self.selected_wire_ids.clear()

    def _clear_selection(self):
        self.selected = None
        self.selected_items.clear()
        self.selected_wire_ids.clear()

    def _select_wire_chain_with_modifier(self, wire_id: str, event) -> bool:
        if not self._has_selection_modifier(event):
            return False
        chain_ids = set(self._wire_ids_for_selection(wire_id, chain=True))
        current_items = self._active_selected_items()
        non_wire_items = {item for item in current_items if item[0] != "wire"}
        current = {ident for kind, ident in current_items if kind == "wire"} or self._valid_selected_wire_ids()
        if not current and self.selected and self.selected[0] == "wire":
            current = set(self._wire_ids_for_selection(self.selected[1], chain=True))
        if self._is_ctrl_pressed(event):
            if chain_ids and chain_ids.issubset(current):
                current -= chain_ids
            else:
                current |= chain_ids
        else:
            current |= chain_ids

        if current:
            primary = wire_id if wire_id in current else sorted(current)[0]
            self._set_selected_items(non_wire_items | {("wire", selected_id) for selected_id in current}, primary=("wire", primary))
            self.load_selection_properties_to_panel()
            self.redraw()
            self.status(f"{len(self._active_selected_items())} object(en) geselecteerd.")
        elif non_wire_items:
            self._set_selected_items(non_wire_items)
            self.load_selection_properties_to_panel()
            self.redraw()
            self.status(f"{len(non_wire_items)} object(en) geselecteerd.")
        else:
            self._clear_selection()
            self.load_selection_properties_to_panel()
            self.redraw()
            self.status("Draadselectie leeg.")
        return True

    def _selected_wire_ids(self) -> List[str]:
        if not self.selected or self.selected[0] != "wire":
            return []
        selected_ids = self._valid_selected_wire_ids()
        if selected_ids:
            return sorted(selected_ids)
        wire_id = self.selected[1]
        if self.wire_move_scope == "chain":
            return self._connected_wire_ids(wire_id)
        return [wire_id]

    def _wire_endpoint_handle_hit(
        self, point: Tuple[float, float], wire: Optional[WirePath] = None
    ) -> Optional[Tuple[WirePath, int]]:
        candidates: List[WirePath] = []
        if wire:
            candidates = [wire]
        elif self.selected and self.selected[0] == "wire":
            selected_wire = self._find_wire(self.selected[1])
            if selected_wire:
                candidates = [selected_wire] + [candidate for candidate in self.wires if candidate.wire_id != selected_wire.wire_id]
        else:
            candidates = list(self.wires)
        if not candidates:
            return None

        tol_mm = max(1.6, 9.0 / max(0.3, self.zoom))
        best_hit: Optional[Tuple[WirePath, int]] = None
        best_dist = float("inf")
        for candidate in candidates:
            endpoints = self._wire_endpoints(candidate)
            if endpoints is None:
                continue
            for endpoint_index, endpoint in enumerate(endpoints):
                dist = math.dist(point, endpoint)
                if dist <= tol_mm and dist < best_dist:
                    best_dist = dist
                    best_hit = (candidate, endpoint_index)
        return best_hit

    def _wire_has_explicit_curve_handles(self, wire: Optional[WirePath]) -> bool:
        if not wire:
            return False
        return self._wire_handle_is_explicit(wire, "start_tangent") or self._wire_handle_is_explicit(
            wire, "end_tangent"
        )

    def _wire_handle_is_explicit(self, wire: Optional[WirePath], handle_name: str) -> bool:
        if not wire:
            return False
        offsets = wire.start_handle_offset_mm if handle_name == "start_tangent" else wire.end_handle_offset_mm
        return any(abs(float(value)) > 1e-4 for value in offsets)

    def _wire_has_curve_handle(self, wire: Optional[WirePath]) -> bool:
        return bool(
            wire
            and normalize_wire_style(wire.style) in {"curve", "twisted_pair_curve"}
            and len(wire.points_mm) >= 2
            and not self._wire_has_explicit_curve_handles(wire)
        )

    def _wire_has_tangent_handles(self, wire: Optional[WirePath]) -> bool:
        return bool(wire and len(wire.points_mm) >= 2)

    def _selected_curve_wire(self) -> Optional[WirePath]:
        if not self.selected or self.selected[0] != "wire":
            return None
        wire = self._find_wire(self.selected[1])
        return wire if self._wire_has_curve_handle(wire) else None

    def _selected_tangent_wire(self) -> Optional[WirePath]:
        if not self.selected or self.selected[0] != "wire":
            return None
        wire = self._find_wire(self.selected[1])
        return wire if self._wire_has_tangent_handles(wire) else None

    def _leader_endpoint_handle_hit(
        self, point: Tuple[float, float], leader: Optional[Leader] = None
    ) -> Optional[Tuple[Leader, int]]:
        if leader:
            candidates = [leader]
        elif self.selected and self.selected[0] == "leader":
            selected_leader = self._find_leader(self.selected[1])
            candidates = ([selected_leader] if selected_leader else []) + [
                candidate for candidate in self.leaders if not selected_leader or candidate.leader_id != selected_leader.leader_id
            ]
        else:
            candidates = list(self.leaders)
        if not candidates:
            return None

        tol_mm = max(1.6, 9.0 / max(0.3, self.zoom))
        best_hit: Optional[Tuple[Leader, int]] = None
        best_dist = float("inf")
        for candidate in candidates:
            for endpoint_index, endpoint in enumerate((candidate.start_mm, candidate.end_mm)):
                dist = math.dist(point, endpoint)
                if dist <= tol_mm and dist < best_dist:
                    best_dist = dist
                    best_hit = (candidate, endpoint_index)
        return best_hit

    def _begin_leader_endpoint_drag(self, leader: Leader, endpoint_index: int):
        self._set_single_selection("leader", leader.leader_id)
        self.load_selection_properties_to_panel()
        self.drag_start_world = None
        self.drag_original_world = None
        self.drag_original_points = None
        self.drag_original_wire_points = None
        self.drag_original_items = None
        self.wire_endpoint_drag_state = None
        self.wire_tangent_drag_state = None
        self.wire_curve_drag_state = None
        self.leader_endpoint_drag_state = {
            "leader_id": leader.leader_id,
            "endpoint_index": 0 if endpoint_index <= 0 else 1,
        }
        self._drag_history_before = self._capture_before_change()
        self._drag_history_changed = False
        label = "pijlpunt" if endpoint_index == 0 else "tekstkant"
        self.request_redraw()
        self.status(f"Sleep de {label} van leader {leader.leader_id}.")

    def _begin_wire_endpoint_drag(self, wire: WirePath, endpoint_index: int):
        self._set_primary_wire_selection(wire.wire_id)
        self.load_selection_properties_to_panel()
        self.drag_start_world = None
        self.drag_original_world = None
        self.drag_original_points = None
        self.drag_original_wire_points = None
        self.drag_original_items = None
        self.leader_endpoint_drag_state = None
        self.wire_tangent_drag_state = None
        self.wire_curve_drag_state = None
        members = [(wire.wire_id, endpoint_index)]
        if self.wire_endpoint_drag_scope == "junction":
            members = self._wire_endpoint_group_members(wire.points_mm[0 if endpoint_index == 0 else -1]) or members
        self.wire_endpoint_drag_state = {
            "wire_id": wire.wire_id,
            "endpoint_index": endpoint_index,
            "snap_meta": None,
            "members": members,
        }
        self._drag_history_before = self._capture_before_change()
        self._drag_history_changed = False
        label = "beginpunt" if endpoint_index == 0 else "eindpunt"
        self.request_redraw()
        scope_text = "aangesloten uiteinden mee" if self.wire_endpoint_drag_scope == "junction" else "alleen dit uiteinde"
        self.status(f"Sleep het {label} van {wire.wire_id}. Modus: {scope_text}. Shift = h/v recht.")

    def _wire_endpoint_group_members(self, point: Tuple[float, float], tol: float = 0.05) -> List[Tuple[str, int]]:
        members: List[Tuple[str, int]] = []
        for candidate in self.wires:
            endpoints = self._wire_endpoints(candidate)
            if endpoints is None:
                continue
            for endpoint_index, endpoint in enumerate(endpoints):
                if math.dist(endpoint, point) <= tol:
                    members.append((candidate.wire_id, endpoint_index))
        return members or []

    def _prepare_wire_endpoint_drag_mode(self, wire: WirePath, endpoint_index: int, scope: str):
        self._set_primary_wire_selection(wire.wire_id)
        self._set_wire_endpoint_drag_scope(scope)
        self.load_selection_properties_to_panel()
        self.redraw()
        point = wire.points_mm[0 if endpoint_index == 0 else -1]
        member_count = len(self._wire_endpoint_group_members(point))
        if scope == "junction":
            self.status(f"Knooppuntmodus: sleep dit punt om {max(1, member_count)} aangesloten uiteinde(n) samen te verplaatsen.")
        else:
            label = "beginpunt" if endpoint_index == 0 else "eindpunt"
            self.status(f"Eindpuntmodus: sleep alleen het {label} van {wire.wire_id}.")

    def _add_wire_endpoint_context_menu(self, menu: tk.Menu, wire: WirePath, endpoint_index: int):
        endpoints = self._wire_endpoints(wire)
        if endpoints is None:
            return
        point = endpoints[endpoint_index]
        member_count = len(self._wire_endpoint_group_members(point))
        title = "Knooppunt" if member_count > 1 else "Draaduiteinde"

        endpoint_menu = tk.Menu(menu, tearoff=0)
        endpoint_menu.add_command(
            label="Sleep alleen dit draaduiteinde",
            command=lambda w=wire, i=endpoint_index: self._prepare_wire_endpoint_drag_mode(w, i, "single"),
        )
        endpoint_menu.add_command(
            label=f"Sleep aangesloten uiteinden mee ({max(1, member_count)})",
            command=lambda w=wire, i=endpoint_index: self._prepare_wire_endpoint_drag_mode(w, i, "junction"),
        )
        endpoint_menu.add_separator()
        endpoint_menu.add_radiobutton(
            label="Standaard: alleen dit uiteinde",
            value=wire_endpoint_drag_scope_label("single"),
            variable=self.prop_wire_endpoint_drag_scope_var,
            command=lambda: self._set_wire_endpoint_drag_scope("single", announce=True),
        )
        endpoint_menu.add_radiobutton(
            label="Standaard: aangesloten uiteinden mee",
            value=wire_endpoint_drag_scope_label("junction"),
            variable=self.prop_wire_endpoint_drag_scope_var,
            command=lambda: self._set_wire_endpoint_drag_scope("junction", announce=True),
        )
        menu.add_cascade(label=f"{title}: {wire.wire_id}", menu=endpoint_menu)

    def _wire_tangent_handle_hit(self, point: Tuple[float, float]) -> Optional[Tuple[WirePath, str]]:
        wire = self._selected_tangent_wire()
        if not wire:
            return None
        controls = self._wire_tangent_handle_points(wire)
        tol_mm = max(1.6, 9.0 / max(0.3, self.zoom))
        best_hit: Optional[Tuple[WirePath, str]] = None
        best_dist = float("inf")
        for handle_name, handle_point in controls.items():
            dist = math.dist(point, handle_point)
            if dist <= tol_mm and dist < best_dist:
                best_dist = dist
                best_hit = (wire, handle_name)
        return best_hit

    def _begin_wire_tangent_drag(self, wire: WirePath, handle_name: str):
        self._set_primary_wire_selection(wire.wire_id)
        self.load_selection_properties_to_panel()
        self.drag_start_world = None
        self.drag_original_world = None
        self.drag_original_points = None
        self.drag_original_wire_points = None
        self.drag_original_items = None
        self.leader_endpoint_drag_state = None
        self.wire_endpoint_drag_state = None
        self.wire_curve_drag_state = None
        self.wire_tangent_drag_state = {"wire_id": wire.wire_id, "handle_name": handle_name}
        self._drag_history_before = self._capture_before_change()
        self._drag_history_changed = False
        label = "starttangent" if handle_name == "start_tangent" else "eindtangent"
        self.request_redraw()
        self.status(f"Sleep de {label} van {wire.wire_id} om de kromming lokaal te finetunen.")

    def _begin_connector_label_drag(self, connector: ConnectorInstance, world: Tuple[float, float]):
        self._set_single_selection("connector", connector.connector_id)
        self.load_selection_properties_to_panel()
        self.drag_start_world = world
        self.drag_original_world = None
        self.drag_original_points = None
        self.drag_original_wire_points = None
        self.drag_original_items = None
        self.leader_endpoint_drag_state = None
        self.wire_endpoint_drag_state = None
        self.wire_tangent_drag_state = None
        self.wire_curve_drag_state = None
        self.table_resize_state = None
        self.connector_label_drag_state = {
            "connector_id": connector.connector_id,
            "label0": (connector.label_dx_mm, connector.label_dy_mm),
        }
        self._drag_history_before = self._capture_before_change()
        self._drag_history_changed = False
        self.request_redraw()
        self.status(f"Sleep de naam van {connector.connector_id} naar de gewenste positie.")

    def _curve_handle_hit_wire(self, point: Tuple[float, float]) -> Optional[WirePath]:
        wire = self._selected_curve_wire()
        if not wire:
            return None
        control = self._curve_control_point(wire)
        tol_mm = max(1.6, 9.0 / max(0.3, self.zoom))
        if math.dist(point, control) <= tol_mm:
            return wire
        return None

    def _lerp_point(self, a: Tuple[float, float], b: Tuple[float, float], t: float) -> Tuple[float, float]:
        return (a[0] + (b[0] - a[0]) * t, a[1] + (b[1] - a[1]) * t)

    def _quadratic_points(
        self, start: Tuple[float, float], control: Tuple[float, float], end: Tuple[float, float], samples: int = 18
    ) -> List[Tuple[float, float]]:
        pts: List[Tuple[float, float]] = []
        total = max(4, samples)
        for i in range(total + 1):
            t = i / total
            omt = 1.0 - t
            pts.append(
                (
                    omt * omt * start[0] + 2.0 * omt * t * control[0] + t * t * end[0],
                    omt * omt * start[1] + 2.0 * omt * t * control[1] + t * t * end[1],
                )
            )
        return pts

    def _bridge_curve_points(
        self,
        start: Tuple[float, float],
        end: Tuple[float, float],
        normal: Tuple[float, float],
        height: float,
        samples: int = 18,
    ) -> List[Tuple[float, float]]:
        pts: List[Tuple[float, float]] = []
        total = max(8, samples)
        nx, ny = normal
        for i in range(total + 1):
            t = i / total
            base = self._lerp_point(start, end, t)
            lift = height * (math.sin(math.pi * t) ** 2)
            pts.append((base[0] + nx * lift, base[1] + ny * lift))
        return pts

    def _cubic_point(
        self,
        start: Tuple[float, float],
        control_a: Tuple[float, float],
        control_b: Tuple[float, float],
        end: Tuple[float, float],
        t: float,
    ) -> Tuple[float, float]:
        omt = 1.0 - t
        return (
            omt * omt * omt * start[0]
            + 3.0 * omt * omt * t * control_a[0]
            + 3.0 * omt * t * t * control_b[0]
            + t * t * t * end[0],
            omt * omt * omt * start[1]
            + 3.0 * omt * omt * t * control_a[1]
            + 3.0 * omt * t * t * control_b[1]
            + t * t * t * end[1],
        )

    def _cubic_points(
        self,
        start: Tuple[float, float],
        control_a: Tuple[float, float],
        control_b: Tuple[float, float],
        end: Tuple[float, float],
        samples: int = 24,
    ) -> List[Tuple[float, float]]:
        pts: List[Tuple[float, float]] = []
        total = max(6, samples)
        for i in range(total + 1):
            pts.append(self._cubic_point(start, control_a, control_b, end, i / total))
        return pts

    def _curve_control_point(self, wire: WirePath) -> Tuple[float, float]:
        endpoints = self._wire_endpoints(wire)
        if endpoints is None:
            return (0.0, 0.0)
        (x0, y0), (x1, y1) = endpoints
        dx = x1 - x0
        dy = y1 - y0
        length = math.hypot(dx, dy)
        if length < 1e-9:
            return ((x0 + x1) / 2.0, (y0 + y1) / 2.0)
        nx = -dy / length
        ny = dx / length
        mid_x = (x0 + x1) / 2.0
        mid_y = (y0 + y1) / 2.0
        return (mid_x + nx * wire.curve_offset_mm, mid_y + ny * wire.curve_offset_mm)

    def _wire_default_cubic_control_points(
        self, wire: WirePath, endpoints: Optional[Tuple[Tuple[float, float], Tuple[float, float]]] = None
    ) -> Tuple[Tuple[float, float], Tuple[float, float]]:
        endpoints = endpoints or self._wire_endpoints(wire)
        if endpoints is None:
            return ((0.0, 0.0), (0.0, 0.0))
        start, end = endpoints
        if normalize_wire_style(wire.style) not in {"curve", "twisted_pair_curve"}:
            return (
                (start[0] + (end[0] - start[0]) / 3.0, start[1] + (end[1] - start[1]) / 3.0),
                (end[0] - (end[0] - start[0]) / 3.0, end[1] - (end[1] - start[1]) / 3.0),
            )
        quad_control = self._curve_control_point(wire)
        return (
            (
                start[0] + (2.0 / 3.0) * (quad_control[0] - start[0]),
                start[1] + (2.0 / 3.0) * (quad_control[1] - start[1]),
            ),
            (
                end[0] + (2.0 / 3.0) * (quad_control[0] - end[0]),
                end[1] + (2.0 / 3.0) * (quad_control[1] - end[1]),
            ),
        )

    def _wire_cubic_control_points(self, wire: WirePath) -> Tuple[Tuple[float, float], Tuple[float, float]]:
        endpoints = self._wire_endpoints(wire)
        if endpoints is None:
            return ((0.0, 0.0), (0.0, 0.0))
        start, end = endpoints
        control_a, control_b = self._wire_default_cubic_control_points(wire, endpoints)
        if self._wire_handle_is_explicit(wire, "start_tangent"):
            control_a = (start[0] + wire.start_handle_offset_mm[0], start[1] + wire.start_handle_offset_mm[1])
        if self._wire_handle_is_explicit(wire, "end_tangent"):
            control_b = (end[0] + wire.end_handle_offset_mm[0], end[1] + wire.end_handle_offset_mm[1])
        return (control_a, control_b)

    def _wire_tangent_handle_points(self, wire: WirePath) -> Dict[str, Tuple[float, float]]:
        endpoints = self._wire_endpoints(wire)
        if endpoints is None:
            return {}
        control_a, control_b = self._wire_cubic_control_points(wire)
        return {"start_tangent": control_a, "end_tangent": control_b}

    def _apply_wire_tangent_handle_position(self, wire: WirePath, handle_name: str, control_point: Tuple[float, float]):
        endpoints = self._wire_endpoints(wire)
        if endpoints is None:
            return
        start, end = endpoints
        if handle_name == "start_tangent":
            wire.start_handle_offset_mm = (control_point[0] - start[0], control_point[1] - start[1])
        else:
            wire.end_handle_offset_mm = (control_point[0] - end[0], control_point[1] - end[1])

        if wire.style == "straight":
            wire.style = "curve"
        elif wire.style == "twisted_pair":
            wire.style = "twisted_pair_curve"

        control_a, control_b = self._wire_cubic_control_points(wire)
        wire.curve_offset_mm = round(self._curve_offset_for_cubic_controls(start, end, control_a, control_b), 3)

    def _curve_offset_for_cubic_controls(
        self,
        start: Tuple[float, float],
        end: Tuple[float, float],
        control_a: Tuple[float, float],
        control_b: Tuple[float, float],
    ) -> float:
        midpoint = self._cubic_point(start, control_a, control_b, end, 0.5)
        return self._curve_offset_for_control(start, end, midpoint)

    def _curve_offset_for_control(
        self, start: Tuple[float, float], end: Tuple[float, float], control: Tuple[float, float]
    ) -> float:
        dx = end[0] - start[0]
        dy = end[1] - start[1]
        length = math.hypot(dx, dy)
        if length < 1e-9:
            return 0.0
        nx = -dy / length
        ny = dx / length
        mid_x = (start[0] + end[0]) / 2.0
        mid_y = (start[1] + end[1]) / 2.0
        return (control[0] - mid_x) * nx + (control[1] - mid_y) * ny

    def _wire_endpoint_node_key(self, point: Tuple[float, float], tol: float = 0.05) -> Tuple[int, int]:
        return (round(point[0] / tol), round(point[1] / tol))

    def _ordered_wire_chains_for_smoothing(self, wire_ids: set[str]) -> List[List[Tuple[WirePath, bool]]]:
        wires_by_id: Dict[str, WirePath] = {}
        endpoint_nodes: Dict[str, Tuple[Tuple[int, int], Tuple[int, int]]] = {}
        node_members: Dict[Tuple[int, int], List[Tuple[str, int]]] = {}
        for wire_id in wire_ids:
            wire = self._find_wire(wire_id)
            endpoints = self._wire_endpoints(wire) if wire else None
            if not wire or endpoints is None:
                continue
            start_node = self._wire_endpoint_node_key(endpoints[0])
            end_node = self._wire_endpoint_node_key(endpoints[1])
            if start_node == end_node:
                continue
            wires_by_id[wire_id] = wire
            endpoint_nodes[wire_id] = (start_node, end_node)
            node_members.setdefault(start_node, []).append((wire_id, 0))
            node_members.setdefault(end_node, []).append((wire_id, 1))

        chains: List[List[Tuple[WirePath, bool]]] = []
        remaining = set(wires_by_id)
        while remaining:
            root = next(iter(remaining))
            component: set[str] = set()
            stack = [root]
            while stack:
                current = stack.pop()
                if current in component or current not in wires_by_id:
                    continue
                component.add(current)
                for node in endpoint_nodes[current]:
                    stack.extend(other_id for other_id, _endpoint_index in node_members.get(node, []) if other_id not in component)
            remaining -= component
            chain = self._ordered_wire_chain_component(component, endpoint_nodes, node_members, wires_by_id)
            if chain:
                chains.append(chain)
        return chains

    def _ordered_wire_chain_component(
        self,
        component: set[str],
        endpoint_nodes: Dict[str, Tuple[Tuple[int, int], Tuple[int, int]]],
        node_members: Dict[Tuple[int, int], List[Tuple[str, int]]],
        wires_by_id: Dict[str, WirePath],
    ) -> List[Tuple[WirePath, bool]]:
        if len(component) < 2:
            return []
        node_degree: Dict[Tuple[int, int], int] = {}
        for wire_id in component:
            for node in endpoint_nodes[wire_id]:
                node_degree[node] = node_degree.get(node, 0) + 1
        if any(degree > 2 for degree in node_degree.values()):
            return []
        end_nodes = sorted(node for node, degree in node_degree.items() if degree == 1)
        if len(end_nodes) != 2:
            return []

        current_node = end_nodes[0]
        used: set[str] = set()
        ordered: List[Tuple[WirePath, bool]] = []
        while len(used) < len(component):
            options = sorted(
                (
                    (wire_id, endpoint_index)
                    for wire_id, endpoint_index in node_members.get(current_node, [])
                    if wire_id in component and wire_id not in used
                ),
                key=lambda item: item[0],
            )
            if not options:
                return []
            wire_id, endpoint_index = options[0]
            start_node, end_node = endpoint_nodes[wire_id]
            forward = endpoint_index == 0
            ordered.append((wires_by_id[wire_id], forward))
            used.add(wire_id)
            current_node = end_node if forward else start_node
        return ordered if len(ordered) == len(component) else []

    def _wire_chain_smooth_controls(
        self, points: List[Tuple[float, float]], curve_offset_mm: float
    ) -> List[Tuple[Tuple[float, float], Tuple[float, float]]]:
        if len(points) < 2:
            return []
        segment_lengths = [math.dist(points[i], points[i + 1]) for i in range(len(points) - 1)]
        if not segment_lengths or any(length <= 1e-6 for length in segment_lengths):
            return []

        def unit(a: Tuple[float, float], b: Tuple[float, float]) -> Tuple[float, float]:
            dx = b[0] - a[0]
            dy = b[1] - a[1]
            length = math.hypot(dx, dy)
            if length <= 1e-9:
                return (1.0, 0.0)
            return (dx / length, dy / length)

        tangents: List[Tuple[float, float]] = []
        handle_lengths: List[float] = []
        for idx, point in enumerate(points):
            if idx == 0:
                tangent = unit(points[0], points[1])
                handle_len = segment_lengths[0] / 3.0
            elif idx == len(points) - 1:
                tangent = unit(points[-2], points[-1])
                handle_len = segment_lengths[-1] / 3.0
            else:
                incoming = unit(points[idx - 1], point)
                outgoing = unit(point, points[idx + 1])
                tx = incoming[0] + outgoing[0]
                ty = incoming[1] + outgoing[1]
                length = math.hypot(tx, ty)
                tangent = (tx / length, ty / length) if length > 1e-6 else unit(points[idx - 1], points[idx + 1])
                handle_len = min(segment_lengths[idx - 1], segment_lengths[idx]) / 3.0
            tangents.append(tangent)
            handle_lengths.append(handle_len)

        strength = clamp(abs(curve_offset_mm) / 8.0, 0.0, 1.0)
        controls: List[Tuple[Tuple[float, float], Tuple[float, float]]] = []
        for idx, seg_len in enumerate(segment_lengths):
            start = points[idx]
            end = points[idx + 1]
            straight_a = (start[0] + (end[0] - start[0]) / 3.0, start[1] + (end[1] - start[1]) / 3.0)
            straight_b = (end[0] - (end[0] - start[0]) / 3.0, end[1] - (end[1] - start[1]) / 3.0)
            handle_a = min(seg_len / 3.0, handle_lengths[idx])
            handle_b = min(seg_len / 3.0, handle_lengths[idx + 1])
            smooth_a = (start[0] + tangents[idx][0] * handle_a, start[1] + tangents[idx][1] * handle_a)
            smooth_b = (end[0] - tangents[idx + 1][0] * handle_b, end[1] - tangents[idx + 1][1] * handle_b)
            controls.append((self._lerp_point(straight_a, smooth_a, strength), self._lerp_point(straight_b, smooth_b, strength)))
        return controls

    def _smooth_wire_chain(self, chain: List[Tuple[WirePath, bool]]):
        if len(chain) < 2:
            return
        points: List[Tuple[float, float]] = []
        for idx, (wire, forward) in enumerate(chain):
            endpoints = self._wire_endpoints(wire)
            if endpoints is None:
                return
            start, end = endpoints if forward else (endpoints[1], endpoints[0])
            if idx == 0:
                points.append(start)
            elif math.dist(points[-1], start) > 0.1:
                return
            points.append(end)

        controls = self._wire_chain_smooth_controls(points, chain[0][0].curve_offset_mm)
        if len(controls) != len(chain):
            return
        for (wire, forward), (control_a, control_b) in zip(chain, controls):
            endpoints = self._wire_endpoints(wire)
            if endpoints is None:
                continue
            if forward:
                wire.start_handle_offset_mm = (control_a[0] - endpoints[0][0], control_a[1] - endpoints[0][1])
                wire.end_handle_offset_mm = (control_b[0] - endpoints[1][0], control_b[1] - endpoints[1][1])
            else:
                wire.start_handle_offset_mm = (control_b[0] - endpoints[0][0], control_b[1] - endpoints[0][1])
                wire.end_handle_offset_mm = (control_a[0] - endpoints[1][0], control_a[1] - endpoints[1][1])

    def _smooth_wire_chains_for_style(self, wire_ids: set[str]):
        for chain in self._ordered_wire_chains_for_smoothing(wire_ids):
            self._smooth_wire_chain(chain)

    def _split_quadratic(
        self, p0: Tuple[float, float], c: Tuple[float, float], p2: Tuple[float, float], t: float
    ) -> Tuple[Tuple[Tuple[float, float], Tuple[float, float], Tuple[float, float]], Tuple[Tuple[float, float], Tuple[float, float], Tuple[float, float]]]:
        p01 = self._lerp_point(p0, c, t)
        p12 = self._lerp_point(c, p2, t)
        split_point = self._lerp_point(p01, p12, t)
        return ((p0, p01, split_point), (split_point, p12, p2))

    def _split_cubic(
        self,
        p0: Tuple[float, float],
        c1: Tuple[float, float],
        c2: Tuple[float, float],
        p3: Tuple[float, float],
        t: float,
    ) -> Tuple[
        Tuple[Tuple[float, float], Tuple[float, float], Tuple[float, float], Tuple[float, float]],
        Tuple[Tuple[float, float], Tuple[float, float], Tuple[float, float], Tuple[float, float]],
    ]:
        p01 = self._lerp_point(p0, c1, t)
        p12 = self._lerp_point(c1, c2, t)
        p23 = self._lerp_point(c2, p3, t)
        p012 = self._lerp_point(p01, p12, t)
        p123 = self._lerp_point(p12, p23, t)
        split_point = self._lerp_point(p012, p123, t)
        return ((p0, p01, p012, split_point), (split_point, p123, p23, p3))

    def _wire_centerline_points(self, wire: WirePath, curve_samples: int = 30) -> List[Tuple[float, float]]:
        cache_key = (self._wire_geometry_signature(wire), int(curve_samples))
        cached = self._wire_centerline_cache.get(cache_key)
        if cached is not None:
            return cached
        style = normalize_wire_style(wire.style)
        if style in {"curve", "twisted_pair_curve"}:
            points = self._wire_curve_points(wire, samples=curve_samples)
        else:
            points = list(wire.points_mm)
        self._wire_centerline_cache[cache_key] = points
        return points

    def _polyline_length(self, points: List[Tuple[float, float]]) -> float:
        total = 0.0
        for i in range(len(points) - 1):
            total += math.dist(points[i], points[i + 1])
        return total

    def _point_and_tangent_on_polyline(
        self, points: List[Tuple[float, float]], distance_mm: float
    ) -> Tuple[Tuple[float, float], Tuple[float, float]]:
        if len(points) < 2:
            point = points[0] if points else (0.0, 0.0)
            return (point, (1.0, 0.0))

        remaining = max(0.0, distance_mm)
        last_tangent = (1.0, 0.0)
        for idx in range(len(points) - 1):
            ax, ay = points[idx]
            bx, by = points[idx + 1]
            seg_len = math.dist((ax, ay), (bx, by))
            if seg_len < 1e-9:
                continue
            tangent = ((bx - ax) / seg_len, (by - ay) / seg_len)
            last_tangent = tangent
            if remaining <= seg_len or idx == len(points) - 2:
                t = clamp(remaining / seg_len, 0.0, 1.0)
                return ((ax + (bx - ax) * t, ay + (by - ay) * t), tangent)
            remaining -= seg_len
        return (points[-1], last_tangent)

    def _polyline_slice(self, points: List[Tuple[float, float]], start_mm: float, end_mm: float) -> List[Tuple[float, float]]:
        if len(points) < 2 or end_mm <= start_mm:
            return []
        total = self._polyline_length(points)
        start_mm = clamp(start_mm, 0.0, total)
        end_mm = clamp(end_mm, 0.0, total)
        if end_mm - start_mm <= 1e-6:
            return []

        start_point, _ = self._point_and_tangent_on_polyline(points, start_mm)
        end_point, _ = self._point_and_tangent_on_polyline(points, end_mm)
        result: List[Tuple[float, float]] = [start_point]
        walked = 0.0
        for idx in range(len(points) - 1):
            seg_len = math.dist(points[idx], points[idx + 1])
            if seg_len < 1e-9:
                continue
            seg_end = walked + seg_len
            if start_mm < seg_end < end_mm:
                if math.dist(result[-1], points[idx + 1]) > 1e-6:
                    result.append(points[idx + 1])
            walked = seg_end
        if math.dist(result[-1], end_point) > 1e-6:
            result.append(end_point)
        return result if len(result) >= 2 else []

    def _polyline_without_ranges(
        self, points: List[Tuple[float, float]], ranges: List[Tuple[float, float]]
    ) -> List[List[Tuple[float, float]]]:
        if len(points) < 2:
            return []
        total = self._polyline_length(points)
        if total <= 1e-6:
            return []
        cleaned: List[Tuple[float, float]] = []
        for start_mm, end_mm in sorted(ranges, key=lambda item: item[0]):
            start_mm = clamp(start_mm, 0.0, total)
            end_mm = clamp(end_mm, 0.0, total)
            if end_mm - start_mm <= 1e-6:
                continue
            if cleaned and start_mm <= cleaned[-1][1] + 1e-6:
                cleaned[-1] = (cleaned[-1][0], max(cleaned[-1][1], end_mm))
            else:
                cleaned.append((start_mm, end_mm))

        if not cleaned:
            return [points]

        parts: List[List[Tuple[float, float]]] = []
        cursor = 0.0
        for start_mm, end_mm in cleaned:
            if start_mm - cursor > 1e-6:
                segment = self._polyline_slice(points, cursor, start_mm)
                if len(segment) >= 2:
                    parts.append(segment)
            cursor = max(cursor, end_mm)
        if total - cursor > 1e-6:
            segment = self._polyline_slice(points, cursor, total)
            if len(segment) >= 2:
                parts.append(segment)
        return parts

    def _wire_curve_points(self, wire: WirePath, samples: int = 30) -> List[Tuple[float, float]]:
        endpoints = self._wire_endpoints(wire)
        if endpoints is None:
            return []
        start, end = endpoints
        control_a, control_b = self._wire_cubic_control_points(wire)
        return self._cubic_points(start, control_a, control_b, end, samples=max(8, samples))

    def _wire_twisted_strands(
        self, wire: WirePath, curve_samples: int = 40, density_scale: float = 1.0
    ) -> Tuple[List[Tuple[float, float]], List[Tuple[float, float]]]:
        centerline = self._wire_centerline_points(wire, curve_samples=curve_samples)
        if len(centerline) < 2:
            return ([], [])
        length = self._polyline_length(centerline)
        if length < 1e-6:
            return (list(centerline), list(centerline))

        pitch = max(1.0, abs(wire.twist_pitch_mm))
        gap = max(0.1, abs(wire.pair_gap_mm))
        cycles = length / pitch
        samples = max(16, int(len(centerline) * (2.0 + density_scale)), int(cycles * (20.0 + 14.0 * density_scale)))
        strand_a: List[Tuple[float, float]] = []
        strand_b: List[Tuple[float, float]] = []
        for i in range(samples + 1):
            t = i / max(1, samples)
            (cx, cy), (ux, uy) = self._point_and_tangent_on_polyline(centerline, length * t)
            nx = -uy
            ny = ux
            phase = 2.0 * math.pi * cycles * t
            off = (gap / 2.0) * math.sin(phase)
            strand_a.append((cx + nx * off, cy + ny * off))
            strand_b.append((cx - nx * off, cy - ny * off))
        return (strand_a, strand_b)

    def _wire_display_polylines(self, wire: WirePath, preview: bool = False) -> List[List[Tuple[float, float]]]:
        cache_key = (self._wire_geometry_signature(wire), bool(preview))
        cached = self._wire_polyline_cache.get(cache_key)
        if cached is not None:
            return cached
        style = normalize_wire_style(wire.style)
        if style in {"twisted_pair", "twisted_pair_curve"}:
            if preview:
                a, b = self._wire_twisted_strands(wire, curve_samples=18, density_scale=0.45)
            else:
                a, b = self._wire_twisted_strands(wire, curve_samples=40, density_scale=1.0)
            polylines = [a, b]
        else:
            polylines = [self._wire_centerline_points(wire, curve_samples=(16 if preview else 30))]
        self._wire_polyline_cache[cache_key] = polylines
        return polylines

    def _wire_label_position(self, wire: WirePath) -> Tuple[float, float]:
        pts = self._wire_centerline_points(wire, curve_samples=40)
        if pts:
            return pts[len(pts) // 2]
        endpoints = self._wire_endpoints(wire)
        if endpoints is None:
            return (0.0, 0.0)
        (x0, y0), (x1, y1) = endpoints
        return ((x0 + x1) / 2.0, (y0 + y1) / 2.0)

    def _copy_wire_to_defaults(self, wire: WirePath):
        self.default_wire_color = wire.color
        self.default_wire_color_b = wire.color_b
        self.default_wire_width_mm = max(0.2, wire.width_mm)
        self.default_wire_style = normalize_wire_style(wire.style)
        self.default_wire_curve_offset_mm = wire.curve_offset_mm
        self.default_wire_twist_pitch_mm = max(1.0, wire.twist_pitch_mm)
        self.default_wire_pair_gap_mm = max(0.2, wire.pair_gap_mm)

    def _split_wire_for_junctions(self, wire: WirePath, cuts: List[dict]) -> Dict[str, Tuple[float, float]]:
        grouped: List[dict] = []
        for cut in sorted(cuts, key=lambda item: float(item.get("t", 0.0))):
            t = clamp(float(cut.get("t", 0.0)), 0.0, 1.0)
            if t <= 1e-4 or t >= 1.0 - 1e-4:
                continue
            if grouped and abs(t - grouped[-1]["t"]) <= 1e-4:
                grouped[-1]["keys"].append(cut["key"])
            else:
                grouped.append({"t": t, "keys": [cut["key"]]})
        if not grouped:
            return {}

        try:
            wire_index = self.wires.index(wire)
        except ValueError:
            return {}

        used_ids = [w.wire_id for w in self.wires if w is not wire]

        def next_wire_id() -> str:
            wire_id = self._next_id("W", used_ids)
            used_ids.append(wire_id)
            return wire_id

        style = normalize_wire_style(wire.style)
        junction_points: Dict[str, Tuple[float, float]] = {}
        child_specs: List[dict] = []

        if style in {"curve", "twisted_pair_curve"}:
            endpoints = self._wire_endpoints(wire)
            if endpoints is None:
                return {}
            control_a, control_b = self._wire_cubic_control_points(wire)
            current = (endpoints[0], control_a, control_b, endpoints[1])
            prev_t = 0.0
            for group in grouped:
                global_t = group["t"]
                local_t = clamp((global_t - prev_t) / max(1e-6, 1.0 - prev_t), 0.0, 1.0)
                left, right = self._split_cubic(current[0], current[1], current[2], current[3], local_t)
                child_specs.append(
                    {
                        "start": left[0],
                        "end": left[3],
                        "curve_offset": self._curve_offset_for_cubic_controls(left[0], left[3], left[1], left[2]),
                        "start_handle_offset": (left[1][0] - left[0][0], left[1][1] - left[0][1]),
                        "end_handle_offset": (left[2][0] - left[3][0], left[2][1] - left[3][1]),
                    }
                )
                for key in group["keys"]:
                    junction_points[key] = left[3]
                current = right
                prev_t = global_t
            child_specs.append(
                {
                    "start": current[0],
                    "end": current[3],
                    "curve_offset": self._curve_offset_for_cubic_controls(current[0], current[3], current[1], current[2]),
                    "start_handle_offset": (current[1][0] - current[0][0], current[1][1] - current[0][1]),
                    "end_handle_offset": (current[2][0] - current[3][0], current[2][1] - current[3][1]),
                }
            )
        else:
            endpoints = self._wire_endpoints(wire)
            if endpoints is None:
                return {}
            points = [endpoints[0]]
            for group in grouped:
                point = self._lerp_point(endpoints[0], endpoints[1], group["t"])
                points.append(point)
                for key in group["keys"]:
                    junction_points[key] = point
            points.append(endpoints[1])
            for idx in range(len(points) - 1):
                child_specs.append(
                    {
                        "start": points[idx],
                        "end": points[idx + 1],
                        "curve_offset": wire.curve_offset_mm,
                        "start_handle_offset": (0.0, 0.0),
                        "end_handle_offset": (0.0, 0.0),
                    }
                )

        children: List[WirePath] = []
        for idx, spec in enumerate(child_specs):
            start = spec["start"]
            end = spec["end"]
            curve_offset = spec["curve_offset"]
            if math.dist(start, end) < 1e-6:
                continue
            children.append(
                WirePath(
                    wire_id=next_wire_id(),
                    points_mm=[start, end],
                    color=wire.color,
                    color_b=wire.color_b,
                    width_mm=wire.width_mm,
                    style=style,
                    curve_offset_mm=curve_offset,
                    start_handle_offset_mm=spec["start_handle_offset"],
                    end_handle_offset_mm=spec["end_handle_offset"],
                    twist_pitch_mm=wire.twist_pitch_mm,
                    pair_gap_mm=wire.pair_gap_mm,
                    label=wire.label if idx == 0 else "",
                    **wire_electrical_kwargs(wire),
                )
            )
        if len(children) < 2:
            return {}
        self.wires = self.wires[:wire_index] + children + self.wires[wire_index + 1 :]
        return junction_points

    def _apply_wire_segment_junctions(self, start_meta: Optional[dict], end_meta: Optional[dict]) -> Dict[str, Tuple[float, float]]:
        grouped: Dict[str, List[dict]] = {}
        for key, meta in (("start", start_meta), ("end", end_meta)):
            if not meta or meta.get("kind") != "wire_segment":
                continue
            grouped.setdefault(str(meta["wire_id"]), []).append({"key": key, "t": float(meta.get("param_t", 0.0))})

        junction_points: Dict[str, Tuple[float, float]] = {}
        for wire_id, cuts in grouped.items():
            wire = self._find_wire(wire_id)
            if not wire:
                continue
            junction_points.update(self._split_wire_for_junctions(wire, cuts))
        return junction_points

    def continue_selected_wire_from_endpoint(self, endpoint_index: int):
        if not self.selected or self.selected[0] != "wire":
            return
        wire = self._find_wire(self.selected[1])
        if not wire or len(wire.points_mm) < 2:
            return
        anchor = wire.points_mm[0] if endpoint_index <= 0 else wire.points_mm[-1]
        self._copy_wire_to_defaults(wire)
        self.set_mode("draw_wire")
        self.begin_wire_chain_at(anchor)
        self._set_primary_wire_selection(wire.wire_id)
        self.load_selection_properties_to_panel()
        label = "beginpunt" if endpoint_index <= 0 else "eindpunt"
        self.status(f"Verder tekenen vanaf {label} van {wire.wire_id}. Nieuwe segmenten nemen dezelfde draadstijl over.")

    def begin_wire_chain_at(self, point: Tuple[float, float], snap_meta: Optional[dict] = None):
        self.temp_wire_points = [point]
        self.temp_wire_anchor_meta = dict(snap_meta) if snap_meta else None
        self.temp_wire_segment_history = []
        self.status(f"Draadstartpunt ingesteld op x={point[0]:.1f}, y={point[1]:.1f}")
        self.redraw()

    def _add_wire_segment(
        self, start: Tuple[float, float], end: Tuple[float, float], start_meta: Optional[dict] = None, end_meta: Optional[dict] = None
    ) -> Optional[dict]:
        if math.dist(start, end) < 1e-6:
            return None
        if self.mode == "draw_wire":
            self._sync_wire_defaults_from_property_panel()
        before = self._capture_before_change()
        junction_points = self._apply_wire_segment_junctions(start_meta, end_meta)
        actual_start = junction_points.get("start", start)
        actual_end = junction_points.get("end", end)
        if math.dist(actual_start, actual_end) < 1e-6:
            self._history_replaying = True
            try:
                self._load_project_dict(json.loads(before))
            finally:
                self._history_replaying = False
            return None
        wire_id = self._next_id("W", [w.wire_id for w in self.wires])
        self.wires.append(
            WirePath(
                wire_id=wire_id,
                points_mm=[actual_start, actual_end],
                color=self.default_wire_color,
                color_b=self.default_wire_color_b,
                width_mm=self.default_wire_width_mm,
                style=self.default_wire_style,
                curve_offset_mm=self.default_wire_curve_offset_mm,
                twist_pitch_mm=self.default_wire_twist_pitch_mm,
                pair_gap_mm=self.default_wire_pair_gap_mm,
                label="",
            )
        )
        self._set_primary_wire_selection(wire_id)
        self.load_selection_properties_to_panel()
        self._commit_change(before)
        return {
            "wire_id": wire_id,
            "before_snapshot": before,
            "start": actual_start,
            "end": actual_end,
            "start_meta": dict(start_meta) if start_meta else None,
            "end_meta": dict(end_meta) if end_meta else None,
        }

    def undo_last_wire_segment(self):
        if not self.temp_wire_segment_history:
            return
        current_snapshot = self._capture_before_change()
        last = self.temp_wire_segment_history.pop()
        previous_snapshot = last.get("before_snapshot")
        if previous_snapshot:
            self._history_replaying = True
            try:
                self._load_project_dict(json.loads(previous_snapshot))
            finally:
                self._history_replaying = False
        start = tuple(last.get("start", (0.0, 0.0)))
        self.temp_wire_points = [start]
        self.temp_wire_anchor_meta = dict(last.get("start_meta")) if last.get("start_meta") else None
        self._clear_selection()
        self.load_selection_properties_to_panel()
        self.status("Laatste draadsegment verwijderd.")
        self.redraw()
        self._commit_change(current_snapshot)

    # ---------------- Events ----------------
    def on_left_down(self, event):
        raw_world = self.canvas_to_world(event.x, event.y)
        world = raw_world
        selection_modifier = self._has_selection_modifier(event)
        if self.mode in {"place_connector", "draw_wire", "draw_leader", "draw_dimension", "draw_table"}:
            world = self._snap_world(raw_world, event)
        self.cursor_world = world if self.mode in {"place_connector", "draw_wire", "draw_leader", "draw_dimension", "draw_table"} else raw_world

        if self.mode == "select" and not selection_modifier and not self.selected_items and self.selected and self.selected[0] == "table":
            table = self._find_table(self.selected[1])
            if table:
                handle = self._find_table_resize_handle(table, raw_world[0], raw_world[1])
                if handle:
                    axis, idx = handle
                    widths = self._table_col_widths(table)
                    heights = self._table_row_heights(table)
                    self._drag_history_before = self._capture_before_change()
                    self._drag_history_changed = False
                    if axis == "col":
                        self.table_resize_state = {
                            "table_id": table.table_id,
                            "axis": "col",
                            "index": idx,
                            "start": raw_world[0],
                            "left0": widths[idx],
                            "right0": widths[idx + 1],
                        }
                        self.status("Sleep om kolombreedtes te wijzigen.")
                    else:
                        self.table_resize_state = {
                            "table_id": table.table_id,
                            "axis": "row",
                            "index": idx,
                            "start": raw_world[1],
                            "left0": heights[idx],
                            "right0": heights[idx + 1],
                        }
                        self.status("Sleep om rijhoogtes te wijzigen.")
                    return

        if self.mode == "select" and not selection_modifier and not self.selected_items:
            label_conn = self._connector_label_handle_hit(raw_world)
            if label_conn:
                self._begin_connector_label_drag(label_conn, raw_world)
                return
            if self.selected and self.selected[0] == "wire":
                endpoint_hit = self._wire_endpoint_handle_hit(raw_world)
                if endpoint_hit:
                    self._begin_wire_endpoint_drag(endpoint_hit[0], endpoint_hit[1])
                    return
                leader_endpoint_hit = self._leader_endpoint_handle_hit(raw_world)
                if leader_endpoint_hit:
                    self._begin_leader_endpoint_drag(leader_endpoint_hit[0], leader_endpoint_hit[1])
                    return
            else:
                leader_endpoint_hit = self._leader_endpoint_handle_hit(raw_world)
                if leader_endpoint_hit:
                    self._begin_leader_endpoint_drag(leader_endpoint_hit[0], leader_endpoint_hit[1])
                    return
                endpoint_hit = self._wire_endpoint_handle_hit(raw_world)
                if endpoint_hit:
                    self._begin_wire_endpoint_drag(endpoint_hit[0], endpoint_hit[1])
                    return
            tangent_hit = self._wire_tangent_handle_hit(raw_world)
            if tangent_hit:
                self._begin_wire_tangent_drag(tangent_hit[0], tangent_hit[1])
                return
            curve_wire = self._curve_handle_hit_wire(raw_world)
            if curve_wire:
                self._set_primary_wire_selection(curve_wire.wire_id)
                self.load_selection_properties_to_panel()
                self.drag_start_world = None
                self.drag_original_world = None
                self.drag_original_points = None
                self.drag_original_wire_points = None
                self.drag_original_items = None
                self.leader_endpoint_drag_state = None
                self.wire_endpoint_drag_state = None
                self.wire_tangent_drag_state = None
                self.wire_curve_drag_state = {"wire_id": curve_wire.wire_id}
                self._drag_history_before = self._capture_before_change()
                self._drag_history_changed = False
                self.request_redraw()
                self.status("Sleep aan de bochthandle. Voor exact: vul Bocht (mm) in of gebruik 'Bochtwaarde invoeren'.")
                return

        if self.mode == "place_connector":
            self.place_connector_at(world[0], world[1])
            return

        if self.mode == "draw_wire":
            snapped, snap_meta = self._wire_snap_if_needed(world, event)
            if not self.temp_wire_points:
                self.begin_wire_chain_at(snapped, snap_meta)
                self.cursor_world = snapped
                return
            start = self.temp_wire_points[-1]
            history_entry = self._add_wire_segment(start, snapped, self.temp_wire_anchor_meta, snap_meta)
            if history_entry:
                self.temp_wire_segment_history.append(history_entry)
                self.temp_wire_points = [history_entry["end"]]
                self.temp_wire_anchor_meta = None
                self.cursor_world = history_entry["end"]
                self.status(f"Segment {history_entry['wire_id']} geplaatst. Klik voor volgend endpoint, ENTER om te stoppen.")
                self.redraw()
            return

        if self.mode == "draw_leader":
            if self.temp_leader_start is None:
                self.temp_leader_start = world
                self.status("Klik eindpunt voor leader.")
            else:
                self.add_leader(self.temp_leader_start, world)
                self.temp_leader_start = None
            self.redraw()
            return

        if self.mode == "draw_dimension":
            if self.temp_dimension_start is None:
                self.temp_dimension_start = world
                self.status("Maatlijn: klik het tweede meetpunt.")
            else:
                self.add_dimension(self.temp_dimension_start, world)
                self.temp_dimension_start = None
            self.redraw()
            return

        if self.mode == "draw_table":
            if self.temp_table_start is None:
                self.temp_table_start = world
                self.status("Klik tweede hoek voor tabel.")
            else:
                self.add_table_from_corners(self.temp_table_start, world)
                self.temp_table_start = None
            self.redraw()
            return

        hit = self.hit_test(raw_world[0], raw_world[1])
        if hit and self._select_item_with_modifier(hit, event):
            self.drag_start_world = None
            self.drag_original_wire_points = None
            self.drag_original_items = None
            self._drag_history_before = None
            self._drag_history_changed = False
            return
        if not hit:
            self._begin_box_select(raw_world, event)
            return

        current_items = self._active_selected_items()
        if self.selected_items and hit in current_items:
            self._set_selected_items(current_items, primary=hit)
        elif hit and hit[0] == "wire":
            self._set_primary_wire_selection(hit[1])
        else:
            self._set_single_selection(hit[0], hit[1])
        self.load_selection_properties_to_panel()
        self.drag_start_world = raw_world
        self.drag_original_wire_points = None
        self.drag_original_items = None
        active_items = self._active_selected_items()
        if self.selected_items and len(active_items) > 1 and hit in active_items:
            originals = self._capture_drag_original_items(active_items)
            if originals:
                self.drag_original_world = None
                self.drag_original_points = None
                self.drag_original_wire_points = None
                self.drag_original_items = originals
                self._drag_history_before = self._capture_before_change()
                self._drag_history_changed = False
                self.redraw()
                return
        if hit and hit[0] == "connector":
            obj = self._find_connector(hit[1])
            if obj:
                self.drag_original_world = (obj.x_mm, obj.y_mm)
                self.drag_original_points = None
                self._drag_history_before = self._capture_before_change()
                self._drag_history_changed = False
        elif hit and hit[0] == "wire":
            obj = self._find_wire(hit[1])
            endpoint_hit = self._wire_endpoint_handle_hit(raw_world, obj) if obj else None
            if endpoint_hit:
                self._begin_wire_endpoint_drag(endpoint_hit[0], endpoint_hit[1])
                return
            wire_ids = self._selected_wire_ids()
            originals: Dict[str, List[Tuple[float, float]]] = {}
            for wire_id in wire_ids:
                obj = self._find_wire(wire_id)
                if obj:
                    originals[wire_id] = list(obj.points_mm)
            if originals:
                self.drag_original_world = None
                self.drag_original_points = None
                self.drag_original_wire_points = originals
                self._drag_history_before = self._capture_before_change()
                self._drag_history_changed = False
        elif hit and hit[0] == "leader":
            obj = self._find_leader(hit[1])
            if obj:
                self.drag_original_world = None
                self.drag_original_points = [obj.start_mm, obj.end_mm]
                self._drag_history_before = self._capture_before_change()
                self._drag_history_changed = False
        elif hit and hit[0] == "dimension":
            obj = self._find_dimension(hit[1])
            if obj:
                self.drag_original_world = None
                self.drag_original_points = [obj.p1_mm, obj.p2_mm]
                self._drag_history_before = self._capture_before_change()
                self._drag_history_changed = False
        elif hit and hit[0] == "text":
            obj = self._find_text_note(hit[1])
            if obj:
                self.drag_original_world = (obj.x_mm, obj.y_mm)
                self.drag_original_points = None
                self._drag_history_before = self._capture_before_change()
                self._drag_history_changed = False
        elif hit and hit[0] == "image":
            obj = self._find_image_note(hit[1])
            if obj:
                self.drag_original_world = (obj.x_mm, obj.y_mm)
                self.drag_original_points = None
                self._drag_history_before = self._capture_before_change()
                self._drag_history_changed = False
        elif hit and hit[0] == "table":
            obj = self._find_table(hit[1])
            if obj:
                self.drag_original_world = (obj.x_mm, obj.y_mm)
                self.drag_original_points = None
                self._drag_history_before = self._capture_before_change()
                self._drag_history_changed = False
        else:
            self.drag_original_world = None
            self.drag_original_points = None
            self.drag_original_wire_points = None
            self.drag_original_items = None
            self._drag_history_before = None
            self._drag_history_changed = False
        self.redraw()

    def _drag_active_id_set(self):
        """De (kind, id)-set die nu als geheel versleept wordt, of None als incrementeel
        slepen niet van toepassing is."""
        if self.drag_original_items:
            return set(self.drag_original_items)
        if not self.selected:
            return None
        if self.selected[0] == "wire" and self.drag_original_wire_points:
            return {("wire", wid) for wid in self.drag_original_wire_points}
        return {(self.selected[0], self.selected[1])}

    def _drag_render_update(self):
        """Hertekenen tijdens een object-drag: incrementeel (alleen het versleepte object)
        als dat kan, anders een gewone gedebouncede redraw."""
        if self._drag_incremental:
            self._update_incremental_drag()
        elif not self._begin_incremental_drag():
            self.request_redraw()

    def _begin_incremental_drag(self) -> bool:
        ids = self._drag_active_id_set()
        if not ids:
            return False
        self._cancel_pending_redraw()
        self._drag_active_ids = ids
        self._drag_incremental = True
        # Achtergrond (alles behalve het versleepte object) één keer tekenen en bevriezen.
        self._drag_filter = ("skip", ids)
        try:
            self.redraw()
        finally:
            self._drag_filter = None
        self._drag_bg_image_refs = list(self._canvas_image_refs)
        self._draw_drag_layer()
        return True

    def _draw_drag_layer(self):
        """Teken alléén de versleepte objecten in een losse 'dragmove'-laag bovenop de
        bevroren achtergrond. Tk hoeft dan enkel de kleine vuile regio te rasteren."""
        cv = self.canvas
        self._canvas_image_refs = list(self._drag_bg_image_refs)
        self._drag_filter = ("only", self._drag_active_ids)
        kinds = {kind for kind, _ident in self._drag_active_ids}
        before = set(cv.find_all())
        try:
            if "image" in kinds:
                self._draw_image_notes()
            if "connector" in kinds:
                self._draw_connectors()
            if "wire" in kinds:
                self._draw_wires()
            if "leader" in kinds:
                self._draw_leaders()
            if "dimension" in kinds:
                self._draw_dimensions()
            if "table" in kinds:
                self._draw_tables()
            if "text" in kinds:
                self._draw_text_notes()
        finally:
            self._drag_filter = None
        for item in cv.find_all():
            if item not in before:
                cv.addtag_withtag("dragmove", item)

    def _update_incremental_drag(self):
        self.canvas.delete("dragmove")
        self._draw_drag_layer()

    def _end_incremental_drag(self):
        if not self._drag_incremental:
            return
        self._drag_incremental = False
        self._drag_active_ids = None
        self._drag_bg_image_refs = []
        self.canvas.delete("dragmove")
        self.redraw()

    def on_left_drag(self, event):
        if self.box_select_state:
            self.box_select_state["current"] = self.canvas_to_world(event.x, event.y)
            self.cursor_world = self.box_select_state["current"]
            self._redraw_temporary_geometry_only()
            return

        if self.table_resize_state:
            state = self.table_resize_state
            table = self._find_table(state["table_id"])
            if not table:
                self.table_resize_state = None
                return
            cur = self.canvas_to_world(event.x, event.y)
            delta = cur[0] - state["start"] if state["axis"] == "col" else cur[1] - state["start"]
            min_size = 3.0
            total = state["left0"] + state["right0"]
            new_left = clamp(state["left0"] + delta, min_size, total - min_size)
            new_right = total - new_left
            idx = state["index"]

            if state["axis"] == "col":
                widths = self._table_col_widths(table)
                widths[idx] = new_left
                widths[idx + 1] = new_right
                table.col_widths_mm = widths
                table.cell_w_mm = sum(widths) / max(1, len(widths))
            else:
                heights = self._table_row_heights(table)
                heights[idx] = new_left
                heights[idx + 1] = new_right
                table.row_heights_mm = heights
                table.cell_h_mm = sum(heights) / max(1, len(heights))
            self._drag_history_changed = True
            self.request_redraw()
            return

        if self.leader_endpoint_drag_state:
            state = self.leader_endpoint_drag_state
            leader = self._find_leader(state["leader_id"])
            if not leader:
                self.leader_endpoint_drag_state = None
                return
            endpoint_index = 0 if int(state.get("endpoint_index", 0)) <= 0 else 1
            current_endpoint = leader.start_mm if endpoint_index == 0 else leader.end_mm
            cur = self._snap_world(self.canvas_to_world(event.x, event.y), event, exclude_points={current_endpoint})
            if endpoint_index == 0:
                if math.dist(leader.start_mm, cur) <= 1e-4:
                    return
                leader.start_mm = cur
                label = "Pijlpunt"
            else:
                if math.dist(leader.end_mm, cur) <= 1e-4:
                    return
                leader.end_mm = cur
                label = "Tekstkant"
            self._drag_history_changed = True
            self.status(f"{label} {leader.leader_id}: x={cur[0]:.1f} mm, y={cur[1]:.1f} mm")
            self.request_redraw()
            return

        if self.wire_endpoint_drag_state:
            state = self.wire_endpoint_drag_state
            wire = self._find_wire(state["wire_id"])
            if not wire:
                self.wire_endpoint_drag_state = None
                return
            endpoint_index = 0 if int(state.get("endpoint_index", 0)) <= 0 else 1
            cur = self.canvas_to_world(event.x, event.y)
            cur, constrained = self._wire_endpoint_drag_constrained_point(cur, event, wire, endpoint_index)
            members = list(state.get("members", [(wire.wire_id, endpoint_index)]))
            exclude_wire_ids = {member_wire_id for member_wire_id, _member_endpoint in members}
            snapped, snap_meta = self._wire_snap_if_needed(
                cur,
                event,
                exclude_wire_ids=exclude_wire_ids,
                exclude_wire_endpoints={(member_wire_id, member_endpoint_index) for member_wire_id, member_endpoint_index in members},
            )
            if constrained:
                constrained_snapped, _is_constrained = self._wire_endpoint_drag_constrained_point(snapped, event, wire, endpoint_index)
                if math.dist(constrained_snapped, snapped) > 1e-4:
                    snap_meta = None
                snapped = constrained_snapped
            state["snap_meta"] = snap_meta
            moved_any = False
            for member_wire_id, member_endpoint_index in members:
                member_wire = self._find_wire(member_wire_id)
                if not member_wire or len(member_wire.points_mm) < 2:
                    continue
                points = list(member_wire.points_mm)
                target_idx = 0 if member_endpoint_index == 0 else -1
                if math.dist(points[target_idx], snapped) <= 1e-4:
                    continue
                points[target_idx] = snapped
                member_wire.points_mm = points
                moved_any = True
            if moved_any:
                self._drag_history_changed = True
                self._clear_wire_geometry_caches()
                label = "beginpunt" if endpoint_index == 0 else "eindpunt"
                self.status(f"{label.capitalize()} {wire.wire_id}: x={snapped[0]:.1f} mm, y={snapped[1]:.1f} mm")
                self.request_redraw()
            return

        if self.wire_tangent_drag_state:
            state = self.wire_tangent_drag_state
            wire = self._find_wire(state["wire_id"])
            if not wire:
                self.wire_tangent_drag_state = None
                return
            control_point = self.canvas_to_world(event.x, event.y)
            self._apply_wire_tangent_handle_position(wire, state["handle_name"], control_point)
            self._drag_history_changed = True
            if self.selected == ("wire", wire.wire_id):
                self.prop_curve_var.set(f"{wire.curve_offset_mm:g}")
            self._clear_wire_geometry_caches()
            self.request_redraw()
            return

        if self.wire_curve_drag_state:
            state = self.wire_curve_drag_state
            wire = self._find_wire(state["wire_id"])
            if not wire:
                self.wire_curve_drag_state = None
                return
            endpoints = self._wire_endpoints(wire)
            if endpoints is None:
                return
            cur = self.canvas_to_world(event.x, event.y)
            new_offset = round(self._curve_offset_for_control(endpoints[0], endpoints[1], cur), 3)
            if abs(new_offset - wire.curve_offset_mm) > 1e-4:
                wire.curve_offset_mm = new_offset
                self._drag_history_changed = True
                if self.selected == ("wire", wire.wire_id):
                    self.prop_curve_var.set(f"{new_offset:g}")
                self._clear_wire_geometry_caches()
                self.status(f"Bocht {wire.wire_id}: {new_offset:.1f} mm")
                self.request_redraw()
            return

        if self.connector_label_drag_state:
            state = self.connector_label_drag_state
            conn = self._find_connector(state["connector_id"])
            if not conn:
                self.connector_label_drag_state = None
                return
            cur = self.canvas_to_world(event.x, event.y)
            start = self.drag_start_world or cur
            dx = cur[0] - start[0]
            dy = cur[1] - start[1]
            label0 = state["label0"]
            conn.label_dx_mm = round(label0[0] + dx, 3)
            conn.label_dy_mm = round(label0[1] + dy, 3)
            self.prop_connector_label_dx_var.set(f"{conn.label_dx_mm:g}")
            self.prop_connector_label_dy_var.set(f"{conn.label_dy_mm:g}")
            self._drag_history_changed = True
            self.status(f"Naam {conn.connector_id}: offset x={conn.label_dx_mm:.1f} mm, y={conn.label_dy_mm:.1f} mm")
            self.request_redraw()
            return

        if self.mode != "select":
            return
        if not self.selected or not self.drag_start_world:
            return
        cur = self.canvas_to_world(event.x, event.y)
        dx = cur[0] - self.drag_start_world[0]
        dy = cur[1] - self.drag_start_world[1]
        if self.drag_original_items:
            if self._apply_drag_original_items(self.drag_original_items, dx, dy):
                self._drag_history_changed = True
                if any(kind == "wire" for kind, _ident in self.drag_original_items):
                    self._clear_wire_geometry_caches()
                self._drag_render_update()
            return
        if self.selected[0] == "connector":
            obj = self._find_connector(self.selected[1])
            if obj and self.drag_original_world:
                obj.x_mm = self.drag_original_world[0] + dx
                obj.y_mm = self.drag_original_world[1] + dy
                self._drag_history_changed = True
        elif self.selected[0] == "wire":
            if self.drag_original_wire_points:
                for wire_id, points in self.drag_original_wire_points.items():
                    obj = self._find_wire(wire_id)
                    if not obj:
                        continue
                    obj.points_mm = [(px + dx, py + dy) for px, py in points]
                self._drag_history_changed = True
        elif self.selected[0] == "leader":
            obj = self._find_leader(self.selected[1])
            if obj and self.drag_original_points and len(self.drag_original_points) >= 2:
                obj.start_mm = (self.drag_original_points[0][0] + dx, self.drag_original_points[0][1] + dy)
                obj.end_mm = (self.drag_original_points[1][0] + dx, self.drag_original_points[1][1] + dy)
                self._drag_history_changed = True
        elif self.selected[0] == "dimension":
            obj = self._find_dimension(self.selected[1])
            if obj and self.drag_original_points and len(self.drag_original_points) >= 2:
                obj.p1_mm = (self.drag_original_points[0][0] + dx, self.drag_original_points[0][1] + dy)
                obj.p2_mm = (self.drag_original_points[1][0] + dx, self.drag_original_points[1][1] + dy)
                self._drag_history_changed = True
        elif self.selected[0] == "text":
            obj = self._find_text_note(self.selected[1])
            if obj and self.drag_original_world:
                obj.x_mm = self.drag_original_world[0] + dx
                obj.y_mm = self.drag_original_world[1] + dy
                self._drag_history_changed = True
        elif self.selected[0] == "image":
            obj = self._find_image_note(self.selected[1])
            if obj and self.drag_original_world:
                obj.x_mm = self.drag_original_world[0] + dx
                obj.y_mm = self.drag_original_world[1] + dy
                self._drag_history_changed = True
        elif self.selected[0] == "table":
            obj = self._find_table(self.selected[1])
            if obj and self.drag_original_world:
                obj.x_mm = self.drag_original_world[0] + dx
                obj.y_mm = self.drag_original_world[1] + dy
                self._drag_history_changed = True
        if self._drag_history_changed:
            if self.selected[0] == "wire":
                self._clear_wire_geometry_caches()
            self._drag_render_update()

    def on_left_up(self, _event):
        if self.box_select_state:
            self._finish_box_select()
            return
        had_leader_endpoint_drag = self.leader_endpoint_drag_state is not None
        had_endpoint_drag = self.wire_endpoint_drag_state is not None
        had_tangent_drag = self.wire_tangent_drag_state is not None
        had_curve_drag = self.wire_curve_drag_state is not None
        had_label_drag = self.connector_label_drag_state is not None
        if self.wire_endpoint_drag_state:
            state = self.wire_endpoint_drag_state
            wire = self._find_wire(state["wire_id"])
            snap_meta = state.get("snap_meta")
            if wire and snap_meta and snap_meta.get("kind") == "wire_segment":
                junction_points = self._apply_wire_segment_junctions(snap_meta, None)
                snapped_point = junction_points.get("start")
                if snapped_point:
                    for member_wire_id, member_endpoint_index in state.get("members", [(wire.wire_id, int(state.get("endpoint_index", 0)))]):
                        member_wire = self._find_wire(member_wire_id)
                        if not member_wire or len(member_wire.points_mm) < 2:
                            continue
                        points = list(member_wire.points_mm)
                        points[0 if member_endpoint_index == 0 else -1] = snapped_point
                        member_wire.points_mm = points
                        self._drag_history_changed = True
        if self._drag_history_changed:
            self._commit_change(self._drag_history_before)
        self._end_incremental_drag()
        self._drag_history_before = None
        self._drag_history_changed = False
        self.drag_start_world = None
        self.drag_original_world = None
        self.drag_original_points = None
        self.drag_original_wire_points = None
        self.drag_original_items = None
        self.leader_endpoint_drag_state = None
        self.wire_endpoint_drag_state = None
        self.wire_tangent_drag_state = None
        self.wire_curve_drag_state = None
        self.table_resize_state = None
        self.connector_label_drag_state = None
        if had_label_drag and self.selected and self.selected[0] == "connector":
            self.load_selection_properties_to_panel()
            self.request_redraw()
        if had_leader_endpoint_drag and self.selected and self.selected[0] == "leader":
            self.load_selection_properties_to_panel()
            self.request_redraw()
        if (had_endpoint_drag or had_tangent_drag or had_curve_drag) and self.selected and self.selected[0] == "wire":
            self.load_selection_properties_to_panel()
            self.request_redraw()

    def on_double_click(self, event):
        if self.mode == "draw_wire":
            self.finish_wire()
            return
        if self.mode == "select":
            self.cursor_world = self.canvas_to_world(event.x, event.y)
            if self.edit_table_cell_at_cursor():
                return
            hit = self.hit_test(self.cursor_world[0], self.cursor_world[1])
            if hit:
                if hit[0] == "wire":
                    self._set_primary_wire_selection(hit[1])
                elif hit[0] == "leader":
                    self._set_single_selection(hit[0], hit[1])
                    self.load_selection_properties_to_panel()
                    self.prompt_selected_leader_text()
                    return
                else:
                    self._set_single_selection(hit[0], hit[1])
                self.load_selection_properties_to_panel()
                self.redraw()

    def _popup_menu(self, event, menu: tk.Menu):
        try:
            menu.tk_popup(event.x_root, event.y_root)
        finally:
            menu.grab_release()

    def _show_context_menu_wire_mode(self, event, world: Tuple[float, float], snap_meta: Optional[dict] = None):
        menu = tk.Menu(self, tearoff=0)
        has_anchor = len(self.temp_wire_points) > 0
        has_segments = len(self.temp_wire_segment_history) > 0
        menu.add_command(
            label="Startpunt hier zetten",
            command=lambda: self.begin_wire_chain_at(world, snap_meta),
        )
        menu.add_command(
            label="Laatste segment terugnemen",
            command=self.undo_last_wire_segment,
            state=("normal" if has_segments else "disabled"),
        )
        menu.add_command(
            label="Draadketen stoppen",
            command=self.finish_wire,
            state=("normal" if has_anchor else "disabled"),
        )
        menu.add_separator()
        menu.add_command(label="Undo", command=self.undo, state=("normal" if self._history_undo else "disabled"))
        menu.add_command(label="Redo", command=self.redo, state=("normal" if self._history_redo else "disabled"))
        self._add_application_context_submenus(menu)
        menu.add_separator()
        menu.add_command(label="Naar Selecteer / verplaats", command=lambda: self.set_mode("select"))
        self._popup_menu(event, menu)

    def _show_context_menu_draw_mode(self, event):
        menu = tk.Menu(self, tearoff=0)
        if self.mode == "draw_leader":
            menu.add_command(label="Leader actie annuleren", command=self.cancel_temporary_action)
        elif self.mode == "draw_dimension":
            menu.add_command(label="Maatlijn actie annuleren", command=self.cancel_temporary_action)
        elif self.mode == "draw_table":
            menu.add_command(label="Tabel actie annuleren", command=self.cancel_temporary_action)
        menu.add_separator()
        menu.add_command(label="Undo", command=self.undo, state=("normal" if self._history_undo else "disabled"))
        menu.add_command(label="Redo", command=self.redo, state=("normal" if self._history_redo else "disabled"))
        self._add_application_context_submenus(menu)
        menu.add_separator()
        menu.add_command(label="Naar Selecteer / verplaats", command=lambda: self.set_mode("select"))
        self._popup_menu(event, menu)

    def _show_context_menu_place_connector(self, event, world: Tuple[float, float]):
        menu = tk.Menu(self, tearoff=0)
        can_place = bool(self.active_symbol and self.active_symbol in self.symbols)
        menu.add_command(
            label="Plaats connector hier",
            command=lambda: self.place_connector_at(world[0], world[1]),
            state=("normal" if can_place else "disabled"),
        )
        menu.add_command(label="STEP importeren...", command=self.import_step_symbol)
        menu.add_separator()
        menu.add_command(label="Undo", command=self.undo, state=("normal" if self._history_undo else "disabled"))
        menu.add_command(label="Redo", command=self.redo, state=("normal" if self._history_redo else "disabled"))
        self._add_application_context_submenus(menu)
        menu.add_separator()
        menu.add_command(label="Naar Selecteer / verplaats", command=lambda: self.set_mode("select"))
        self._popup_menu(event, menu)

    def _show_context_menu_select_mode(self, event, world: Tuple[float, float]):
        endpoint_hit = self._wire_endpoint_handle_hit(world)
        hit = self.hit_test(world[0], world[1])
        if endpoint_hit and (hit is None or hit[0] == "wire"):
            hit = ("wire", endpoint_hit[0].wire_id)
        if hit:
            current_items = self._active_selected_items()
            if hit in current_items and len(current_items) > 1:
                self._set_selected_items(current_items, primary=hit)
            elif hit[0] == "wire":
                self._set_primary_wire_selection(hit[1])
            else:
                self._set_single_selection(hit[0], hit[1])
            self.load_selection_properties_to_panel()
            self.redraw()

        menu = tk.Menu(self, tearoff=0)
        if hit:
            kind, ident = hit
            label_kind = {
                "wire": "draad",
                "leader": "leader",
                "dimension": "maatlijn",
                "connector": "connector",
                "table": "tabel",
                "text": "tekst",
                "image": "afbeelding",
            }.get(kind, kind)
            if kind == "table":
                table = self._find_table(ident)
                if table and table.is_border:
                    label_kind = "border"
            active_count = len(self._active_selected_items())
            if active_count > 1:
                menu.add_command(label=f"Eigenschappen: {active_count} geselecteerde objecten", command=self.focus_properties_panel)
                menu.add_command(label=f"Dupliceer primaire {label_kind}", command=self.duplicate_selected)
            else:
                menu.add_command(label=f"Eigenschappen: {label_kind} {ident}", command=self.focus_properties_panel)
                menu.add_command(label=f"Dupliceer {label_kind}", command=self.duplicate_selected)
            if any(k in {"connector", "wire", "leader", "dimension", "table", "text"} for k, _i in self._active_selected_items()):
                menu.add_command(label="Selectie opslaan als blok...", command=self.save_selection_as_block)
            if endpoint_hit and kind == "wire" and endpoint_hit[0].wire_id == ident:
                self._add_wire_endpoint_context_menu(menu, endpoint_hit[0], endpoint_hit[1])

            transform_menu = tk.Menu(menu, tearoff=0)
            transform_menu.add_command(
                label="Roteer +90°",
                command=lambda: self.rotate_selected(90),
                state=("disabled" if kind in {"table", "text", "image"} else "normal"),
            )
            transform_menu.add_command(
                label="Roteer -90°",
                command=lambda: self.rotate_selected(-90),
                state=("disabled" if kind in {"table", "text", "image"} else "normal"),
            )
            transform_menu.add_separator()
            transform_menu.add_command(
                label="Spiegel links/rechts",
                command=lambda: self.mirror_selected("lr"),
                state=("disabled" if kind in {"text", "image"} else "normal"),
            )
            transform_menu.add_command(
                label="Spiegel boven/onder",
                command=lambda: self.mirror_selected("ud"),
                state=("disabled" if kind in {"text", "image"} else "normal"),
            )
            if kind == "connector":
                transform_menu.add_separator()
                transform_menu.add_command(label="Reset connector-transformatie", command=self.reset_selected_transform)
            menu.add_cascade(label="Transformeer", menu=transform_menu)

            order_menu = tk.Menu(menu, tearoff=0)
            order_menu.add_command(label="Naar voorgrond", command=self.bring_selected_to_front)
            order_menu.add_command(label="Naar achtergrond", command=self.send_selected_to_back)
            menu.add_cascade(label="Volgorde", menu=order_menu)

            if kind == "wire":
                wire_menu = tk.Menu(menu, tearoff=0)
                wire_menu.add_command(label="Maak recht", command=lambda: self.set_selected_wire_style("straight"))
                wire_menu.add_command(label="Maak gebogen", command=lambda: self.set_selected_wire_style("curve"))
                wire_menu.add_command(label="Maak twisted pair", command=lambda: self.set_selected_wire_style("twisted_pair"))
                wire_menu.add_command(label="Maak twisted pair gebogen", command=lambda: self.set_selected_wire_style("twisted_pair_curve"))
                wire_menu.add_separator()
                wire_menu.add_command(label="Verplaats alleen segment", command=lambda: self._set_wire_move_scope("segment", announce=True))
                wire_menu.add_command(label="Verplaats hele lijn", command=lambda: self._set_wire_move_scope("chain", announce=True))
                wire_menu.add_separator()
                wire_menu.add_command(label="Sleep eindpunt: alleen dit uiteinde", command=lambda: self._set_wire_endpoint_drag_scope("single", announce=True))
                wire_menu.add_command(label="Sleep eindpunt: aangesloten uiteinden mee", command=lambda: self._set_wire_endpoint_drag_scope("junction", announce=True))
                wire_menu.add_separator()
                wire_menu.add_command(label="Bocht +2 mm", command=lambda: self.adjust_selected_wire_curve(2.0))
                wire_menu.add_command(label="Bocht -2 mm", command=lambda: self.adjust_selected_wire_curve(-2.0))
                wire_menu.add_command(label="Bochtwaarde invoeren...", command=self.prompt_selected_wire_curve_value)
                wire_menu.add_command(label="Keer bochtrichting om", command=self.flip_selected_wire_curve)
                wire_menu.add_command(label="Reset tangenthandles", command=self.reset_selected_wire_tangent_handles)
                wire_menu.add_separator()
                wire_menu.add_command(label="Maak parallelle offset...", command=self.offset_selected_wire_parallel)
                wire_menu.add_separator()
                wire_menu.add_command(label="Verder tekenen vanaf beginpunt", command=lambda: self.continue_selected_wire_from_endpoint(0))
                wire_menu.add_command(label="Verder tekenen vanaf eindpunt", command=lambda: self.continue_selected_wire_from_endpoint(1))
                menu.add_cascade(label="Draadvorm", menu=wire_menu)

            if kind == "leader":
                leader_menu = tk.Menu(menu, tearoff=0)
                leader_menu.add_command(label="Tekst wijzigen...", command=self.prompt_selected_leader_text)
                leader_menu.add_separator()
                leader_menu.add_command(label="Pijlgrootte +0.5 mm", command=lambda: self.adjust_selected_leader_arrow_size(0.5))
                leader_menu.add_command(label="Pijlgrootte -0.5 mm", command=lambda: self.adjust_selected_leader_arrow_size(-0.5))
                leader_menu.add_command(label="Pijlgrootte invoeren...", command=self.prompt_selected_leader_arrow_size)
                menu.add_cascade(label="Leader", menu=leader_menu)

            if kind == "dimension":
                dim_menu = tk.Menu(menu, tearoff=0)
                dim_menu.add_command(label="Horizontaal", command=lambda: self.set_selected_dimension_orientation("horizontal"))
                dim_menu.add_command(label="Verticaal", command=lambda: self.set_selected_dimension_orientation("vertical"))
                dim_menu.add_command(label="Uitgelijnd", command=lambda: self.set_selected_dimension_orientation("aligned"))
                dim_menu.add_separator()
                dim_menu.add_command(label="Keer offset om (andere kant)", command=self.flip_selected_dimension_offset)
                dim_menu.add_command(label="Offset +5 mm", command=lambda: self.adjust_selected_dimension_offset(5.0))
                dim_menu.add_command(label="Offset -5 mm", command=lambda: self.adjust_selected_dimension_offset(-5.0))
                dim_menu.add_separator()
                dim_menu.add_command(label="Tolerantie instellen...", command=self.prompt_selected_dimension_tolerance)
                dim_menu.add_command(label="Maatwaarde overschrijven...", command=self.prompt_selected_dimension_value)
                menu.add_cascade(label="Maatlijn", menu=dim_menu)

            if kind == "table":
                table = self._find_table(ident)
                if not (table and table.is_border):
                    menu.add_command(label="Bewerk tabelcel hier", command=self.edit_table_cell_at_cursor)
                    cell = self._table_cell_from_point(table, world[0], world[1]) if table else None
                    row_idx = cell[0] if cell else 0
                    col_idx = cell[1] if cell else 0

                    table_menu = tk.Menu(menu, tearoff=0)
                    table_menu.add_command(label="Rij boven invoegen", command=lambda: self.table_insert_row(ident, row_idx, below=False))
                    table_menu.add_command(label="Rij onder invoegen", command=lambda: self.table_insert_row(ident, row_idx, below=True))
                    table_menu.add_command(label="Kolom links invoegen", command=lambda: self.table_insert_col(ident, col_idx, right=False))
                    table_menu.add_command(label="Kolom rechts invoegen", command=lambda: self.table_insert_col(ident, col_idx, right=True))
                    table_menu.add_separator()
                    can_remove_row = bool(table and table.rows > 1)
                    can_remove_col = bool(table and table.cols > 1)
                    table_menu.add_command(
                        label="Verwijder huidige rij",
                        command=lambda: self.table_remove_row(ident, row_idx),
                        state=("normal" if can_remove_row else "disabled"),
                    )
                    table_menu.add_command(
                        label="Verwijder huidige kolom",
                        command=lambda: self.table_remove_col(ident, col_idx),
                        state=("normal" if can_remove_col else "disabled"),
                    )
                    table_menu.add_separator()
                    align_h = tk.Menu(table_menu, tearoff=0)
                    align_h.add_command(label="Links", command=lambda: self.table_set_text_align(ident, h_align="left"))
                    align_h.add_command(label="Midden", command=lambda: self.table_set_text_align(ident, h_align="center"))
                    align_h.add_command(label="Rechts", command=lambda: self.table_set_text_align(ident, h_align="right"))
                    align_v = tk.Menu(table_menu, tearoff=0)
                    align_v.add_command(label="Boven", command=lambda: self.table_set_text_align(ident, v_align="top"))
                    align_v.add_command(label="Midden", command=lambda: self.table_set_text_align(ident, v_align="middle"))
                    align_v.add_command(label="Onder", command=lambda: self.table_set_text_align(ident, v_align="bottom"))
                    table_menu.add_cascade(label="Tekst horizontaal", menu=align_h)
                    table_menu.add_cascade(label="Tekst verticaal", menu=align_v)
                    menu.add_cascade(label="Tabel", menu=table_menu)

            delete_label = f"Verwijder selectie ({active_count})" if active_count > 1 else f"Verwijder {label_kind}"
            menu.add_command(label=delete_label, command=self.delete_selected)
            menu.add_separator()

        menu.add_command(label="Undo", command=self.undo, state=("normal" if self._history_undo else "disabled"))
        menu.add_command(label="Redo", command=self.redo, state=("normal" if self._history_redo else "disabled"))
        menu.add_separator()
        menu.add_command(label="Mode: Plaats connector", command=lambda: self.set_mode("place_connector"))
        menu.add_command(label="Mode: Draad tekenen", command=lambda: self.set_mode("draw_wire"))
        menu.add_command(label="Mode: Leader tekenen", command=lambda: self.set_mode("draw_leader"))
        menu.add_command(label="Mode: Tabel plaatsen", command=lambda: self.set_mode("draw_table"))
        self._add_application_context_submenus(menu)
        self._popup_menu(event, menu)

    def on_right_click(self, event):
        world = self.canvas_to_world(event.x, event.y)
        wire_snap_meta = None
        if self.mode in {"place_connector", "draw_wire", "draw_leader", "draw_table"}:
            if self.mode == "draw_wire":
                world, wire_snap_meta = self._wire_snap_if_needed(world, event)
            else:
                world = self._snap_world(world, event)
        self.cursor_world = world

        if self.mode == "draw_wire":
            self._show_context_menu_wire_mode(event, world, wire_snap_meta)
            return
        if self.mode in {"draw_leader", "draw_table"}:
            self._show_context_menu_draw_mode(event)
            return
        if self.mode == "place_connector":
            self._show_context_menu_place_connector(event, world)
            return
        self._show_context_menu_select_mode(event, world)

    def on_middle_down(self, event):
        self.panning = True
        self.pan_start = (event.x, event.y)

    def on_middle_drag(self, event):
        if not self.panning or not self.pan_start:
            return
        dx = event.x - self.pan_start[0]
        dy = event.y - self.pan_start[1]
        self.pan_x += dx
        self.pan_y += dy
        self.pan_start = (event.x, event.y)
        self.request_redraw()

    def on_middle_up(self, _event):
        self.panning = False
        self.pan_start = None

    def on_mouse_wheel(self, event):
        factor = 1.1 if event.delta > 0 else 1 / 1.1
        wx, wy = self.canvas_to_world(event.x, event.y)
        old_zoom = self.zoom
        self.zoom = clamp(self.zoom * factor, 0.3, 8.0)
        if abs(self.zoom - old_zoom) < 1e-9:
            return
        self.pan_x = event.x - wx * self.zoom
        self.pan_y = event.y - wy * self.zoom
        self._redraw_zoom_preview()

    def on_motion(self, event):
        world = self.canvas_to_world(event.x, event.y)
        if self.mode in {"place_connector", "draw_wire", "draw_leader", "draw_table"}:
            if self.mode == "draw_wire":
                snapped_world, _wire_snap_meta = self._wire_snap_if_needed(world, event)
            else:
                snapped_world = self._snap_world(world, event)
        else:
            snapped_world = world
        self.cursor_world = snapped_world
        self.coord_var.set(f"x={snapped_world[0]:.1f} mm   y={snapped_world[1]:.1f} mm")

        if self.mode == "select" and self.selected and self.selected[0] == "table":
            table = self._find_table(self.selected[1])
            handle = self._find_table_resize_handle(table, snapped_world[0], snapped_world[1]) if table else None
            if handle:
                if handle[0] == "col":
                    self.canvas.configure(cursor="sb_h_double_arrow")
                else:
                    self.canvas.configure(cursor="sb_v_double_arrow")
            elif self._leader_endpoint_handle_hit(world):
                self.canvas.configure(cursor="hand2")
            elif self._wire_endpoint_handle_hit(world):
                self.canvas.configure(cursor="hand2")
            elif self._wire_tangent_handle_hit(world):
                self.canvas.configure(cursor="hand2")
            elif self._curve_handle_hit_wire(world):
                self.canvas.configure(cursor="hand2")
            else:
                self.canvas.configure(cursor="arrow")
        elif self.mode == "select" and self._leader_endpoint_handle_hit(world):
            self.canvas.configure(cursor="hand2")
        elif self.mode == "select" and self._wire_endpoint_handle_hit(world):
            self.canvas.configure(cursor="hand2")
        elif self.mode == "select" and self._wire_tangent_handle_hit(world):
            self.canvas.configure(cursor="hand2")
        elif self.mode == "select" and self._curve_handle_hit_wire(world):
            self.canvas.configure(cursor="hand2")
        else:
            self.canvas.configure(cursor="arrow")

        if self.mode in {"draw_wire", "draw_leader", "draw_table"}:
            previous = self._last_preview_cursor_world
            if previous is None or math.dist(previous, snapped_world) > 0.02:
                self._last_preview_cursor_world = snapped_world
                self._redraw_temporary_geometry_only()

    # ---------------- Entity helpers ----------------
    def _next_id(self, prefix: str, existing: List[str]) -> str:
        i = 1
        pool = set(existing)
        while f"{prefix}{i:03d}" in pool:
            i += 1
        return f"{prefix}{i:03d}"

    def _find_connector(self, connector_id: str) -> Optional[ConnectorInstance]:
        for c in self.connectors:
            if c.connector_id == connector_id:
                return c
        return None

    def _find_table(self, table_id: str) -> Optional[TableBox]:
        for t in self.tables:
            if t.table_id == table_id:
                return t
        return None

    def _find_wire(self, wire_id: str) -> Optional[WirePath]:
        for w in self.wires:
            if w.wire_id == wire_id:
                return w
        return None

    def _find_leader(self, leader_id: str) -> Optional[Leader]:
        for l in self.leaders:
            if l.leader_id == leader_id:
                return l
        return None

    def _find_dimension(self, dim_id: str) -> Optional[DimensionLine]:
        for d in self.dimensions:
            if d.dim_id == dim_id:
                return d
        return None

    def _find_text_note(self, note_id: str) -> Optional[TextNote]:
        for note in self.text_notes:
            if note.note_id == note_id:
                return note
        return None

    def _find_image_note(self, image_id: str) -> Optional[ImageNote]:
        for note in self.image_notes:
            if note.image_id == image_id:
                return note
        return None

    def _symbol_instance_cache_id(self, instance) -> str:
        return str(getattr(instance, "connector_id", ""))

    def _connector_geometry_signature(self, connector) -> Tuple:
        return (
            connector.symbol_name,
            round(connector.x_mm, 4),
            round(connector.y_mm, 4),
            round(connector.scale, 4),
            round(connector.rotation_deg % 360.0, 4),
            bool(connector.mirror_x),
            bool(connector.mirror_y),
        )

    def _connector_local_geometry_signature(self, connector) -> Tuple:
        return (
            connector.symbol_name,
            round(connector.scale, 4),
            round(connector.rotation_deg % 360.0, 4),
            bool(connector.mirror_x),
            bool(connector.mirror_y),
        )

    def _symbol_requires_raster_preview(self, sym: StepSymbol) -> bool:
        return (
            polyline_segment_count(sym.polylines) >= CONNECTOR_RASTER_LINE_THRESHOLD
            or polyline_point_count(sym.polylines) >= CONNECTOR_RASTER_POINT_THRESHOLD
        )

    def _connector_canvas_view_signature_value(self) -> Tuple:
        return (round(self.zoom, 5), round(self.pan_x, 3), round(self.pan_y, 3))

    def _ensure_connector_canvas_cache_view(self) -> Tuple:
        view_sig = self._connector_canvas_view_signature_value()
        if view_sig != self._connector_canvas_view_signature:
            self._connector_canvas_view_signature = view_sig
            self._connector_canvas_cache = {}
        return view_sig

    def _connector_world_geometry(self, connector) -> Tuple[List[List[Tuple[float, float]]], Tuple[float, float, float, float]]:
        sig = self._connector_geometry_signature(connector)
        cache_id = self._symbol_instance_cache_id(connector)
        cached = self._connector_world_cache.get(cache_id)
        if cached and cached.get("sig") == sig:
            return (cached["polylines"], cached["bbox"])

        sym = self.symbols.get(connector.symbol_name)
        if not sym or not sym.polylines:
            bbox = (connector.x_mm - 1.0, connector.y_mm - 1.0, connector.x_mm + 1.0, connector.y_mm + 1.0)
            self._connector_world_cache[cache_id] = {"sig": sig, "polylines": [], "bbox": bbox}
            return ([], bbox)

        local_polylines, local_bbox = self._connector_local_geometry(connector)
        world_polylines: List[List[Tuple[float, float]]] = []
        for line in local_polylines:
            world_polylines.append([(connector.x_mm + lx, connector.y_mm + ly) for lx, ly in line])

        bbox = (
            connector.x_mm + local_bbox[0],
            connector.y_mm + local_bbox[1],
            connector.x_mm + local_bbox[2],
            connector.y_mm + local_bbox[3],
        )
        self._connector_world_cache[cache_id] = {"sig": sig, "polylines": world_polylines, "bbox": bbox}
        return (world_polylines, bbox)

    def _connector_local_geometry(self, connector) -> Tuple[List[List[Tuple[float, float]]], Tuple[float, float, float, float]]:
        sig = self._connector_local_geometry_signature(connector)
        cache_id = self._symbol_instance_cache_id(connector)
        cached = self._connector_local_cache.get(cache_id)
        if cached and cached.get("sig") == sig:
            return (cached["polylines"], cached["bbox"])

        sym = self.symbols.get(connector.symbol_name)
        if not sym or not sym.polylines:
            bbox = (-1.0, -1.0, 1.0, 1.0)
            self._connector_local_cache[cache_id] = {"sig": sig, "polylines": [], "bbox": bbox}
            return ([], bbox)

        scale_x = -connector.scale if connector.mirror_x else connector.scale
        scale_y = -connector.scale if connector.mirror_y else connector.scale
        angle = math.radians(connector.rotation_deg)
        ca = math.cos(angle)
        sa = math.sin(angle)

        local_polylines: List[List[Tuple[float, float]]] = []
        xs: List[float] = []
        ys: List[float] = []
        for line in sym.polylines:
            if len(line) < 2:
                continue
            local_line: List[Tuple[float, float]] = []
            for lx, ly in line:
                x = lx * scale_x
                y = ly * scale_y
                rx = x * ca - y * sa
                ry = x * sa + y * ca
                local_line.append((rx, ry))
                xs.append(rx)
                ys.append(ry)
            if len(local_line) >= 2:
                local_polylines.append(local_line)

        bbox = (min(xs), min(ys), max(xs), max(ys)) if xs else (-1.0, -1.0, 1.0, 1.0)
        self._connector_local_cache[cache_id] = {"sig": sig, "polylines": local_polylines, "bbox": bbox}
        return (local_polylines, bbox)

    def _connector_canvas_image(self, connector, stroke_width_px: float):
        if not PIL_AVAILABLE:
            return None
        local_polylines, bbox = self._connector_local_geometry(connector)
        if not local_polylines:
            return None

        x1, y1, x2, y2 = bbox
        pad = max(3, int(math.ceil(stroke_width_px + 2)))
        width_px = max(1, int(math.ceil((x2 - x1) * self.zoom)) + pad * 2)
        height_px = max(1, int(math.ceil((y2 - y1) * self.zoom)) + pad * 2)
        if (
            width_px > CONNECTOR_RASTER_MAX_DIM_PX
            or height_px > CONNECTOR_RASTER_MAX_DIM_PX
            or width_px * height_px > CONNECTOR_RASTER_MAX_AREA_PX
        ):
            return None

        render_sig = (
            self._connector_local_geometry_signature(connector),
            round(self.zoom, 4),
            connector.line_color,
            round(stroke_width_px, 3),
            width_px,
            height_px,
        )
        cache_id = self._symbol_instance_cache_id(connector)
        cached = self._connector_image_cache.get(cache_id)
        if cached and cached.get("sig") == render_sig:
            photo = cached["photo"]
        else:
            image = Image.new("RGBA", (width_px, height_px), (255, 255, 255, 0))
            draw = ImageDraw.Draw(image)
            line_width = max(1, int(round(stroke_width_px)))
            for line in local_polylines:
                points = [((lx - x1) * self.zoom + pad, (ly - y1) * self.zoom + pad) for lx, ly in line]
                if len(points) >= 2:
                    draw.line(points, fill=connector.line_color, width=line_width, joint="curve")
            photo = ImageTk.PhotoImage(image)
            self._connector_image_cache[cache_id] = {"sig": render_sig, "photo": photo}

        left_px = (connector.x_mm + x1) * self.zoom + self.pan_x - pad
        top_px = (connector.y_mm + y1) * self.zoom + self.pan_y - pad
        return (photo, left_px, top_px)

    def _connector_canvas_polylines(self, connector) -> List[Tuple[float, ...]]:
        geom_sig = self._connector_geometry_signature(connector)
        view_sig = self._ensure_connector_canvas_cache_view()
        cache_id = self._symbol_instance_cache_id(connector)
        cached = self._connector_canvas_cache.get(cache_id)
        if cached and cached.get("geom_sig") == geom_sig and cached.get("view_sig") == view_sig:
            return cached["polylines"]

        world_polylines, _bbox = self._connector_world_geometry(connector)
        canvas_polylines: List[Tuple[float, ...]] = []
        for line in world_polylines:
            if len(line) < 2:
                continue
            pts: List[float] = []
            for wx, wy in line:
                pts.extend((wx * self.zoom + self.pan_x, wy * self.zoom + self.pan_y))
            if len(pts) >= 4:
                canvas_polylines.append(tuple(pts))

        self._connector_canvas_cache[cache_id] = {
            "geom_sig": geom_sig,
            "view_sig": view_sig,
            "polylines": canvas_polylines,
        }
        return canvas_polylines

    def _connector_local_to_world(self, connector, lx: float, ly: float) -> Tuple[float, float]:
        x = lx * connector.scale
        y = ly * connector.scale
        if connector.mirror_x:
            x = -x
        if connector.mirror_y:
            y = -y
        angle = math.radians(connector.rotation_deg)
        ca = math.cos(angle)
        sa = math.sin(angle)
        rx = x * ca - y * sa
        ry = x * sa + y * ca
        return (connector.x_mm + rx, connector.y_mm + ry)

    def _connector_world_bbox(self, connector) -> Tuple[float, float, float, float]:
        _polylines, local_bbox = self._connector_local_geometry(connector)
        return (
            connector.x_mm + local_bbox[0],
            connector.y_mm + local_bbox[1],
            connector.x_mm + local_bbox[2],
            connector.y_mm + local_bbox[3],
        )

    def _connector_label_world_pos(self, connector) -> Tuple[float, float]:
        return (connector.x_mm + connector.label_dx_mm, connector.y_mm + connector.label_dy_mm)

    def _connector_label_canvas_bbox(self, connector) -> Tuple[float, float, float, float]:
        cx, cy = self.world_to_canvas(*self._connector_label_world_pos(connector))
        text = connector.connector_id or ""
        half_w = max(10.0, len(text) * 4.0 + 4.0)
        half_h = 8.0
        return (cx - half_w, cy - half_h, cx + half_w, cy + half_h)

    def _connector_label_handle_hit(self, world: Tuple[float, float]) -> Optional[ConnectorInstance]:
        """Geef de connector terug waarvan het naamlabel onder het wereldpunt ligt
        (alleen voor de geselecteerde connector, zodat de handle zichtbaar is)."""
        if not self.selected or self.selected[0] != "connector":
            return None
        conn = self._find_connector(self.selected[1])
        if not conn:
            return None
        px, py = self.world_to_canvas(world[0], world[1])
        x1, y1, x2, y2 = self._connector_label_canvas_bbox(conn)
        if x1 <= px <= x2 and y1 <= py <= y2:
            return conn
        return None

    def _symbol_raw_bbox(self, sym: StepSymbol) -> Tuple[float, float, float, float]:
        xs: List[float] = []
        ys: List[float] = []
        for line in sym.polylines:
            for x, y in line:
                xs.append(x)
                ys.append(y)
        if not xs:
            return (0.0, 0.0, 0.0, 0.0)
        return (min(xs), min(ys), max(xs), max(ys))

    def _connector_pin_local_points(self, connector: ConnectorInstance) -> List[Tuple[str, float, float]]:
        """Pin-posities in symbool-lokale coördinaten (vóór schaal/rotatie).

        Gebruikt expliciete ``pin_offsets_mm`` als die er zijn, anders een
        automatische rij gelijkmatig verdeeld over de breedte van het symbool.
        """
        count = max(1, connector.pin_count)
        labels = connector.pin_labels
        offsets = connector.pin_offsets_mm
        if offsets and len(offsets) >= count:
            return [(connector_pin_label(i, labels), float(offsets[i][0]), float(offsets[i][1])) for i in range(count)]
        sym = self.symbols.get(connector.symbol_name)
        if not sym:
            return [(connector_pin_label(i, labels), 0.0, 0.0) for i in range(count)]
        sx1, sy1, sx2, sy2 = self._symbol_raw_bbox(sym)
        width = sx2 - sx1
        center_y = (sy1 + sy2) / 2.0
        points: List[Tuple[float, float]] = []
        if count == 1:
            points.append(((sx1 + sx2) / 2.0, center_y))
        else:
            margin = width * 0.12
            x0 = sx1 + margin
            x1 = sx2 - margin
            for i in range(count):
                t = i / (count - 1)
                points.append((x0 + t * (x1 - x0), center_y))
        return [(connector_pin_label(i, labels), px, py) for i, (px, py) in enumerate(points)]

    def _connector_pin_world_points(self, connector: ConnectorInstance) -> List[Tuple[str, float, float]]:
        """Pin-posities als (label, wereld-x, wereld-y)."""
        result: List[Tuple[str, float, float]] = []
        for label, lx, ly in self._connector_pin_local_points(connector):
            wx, wy = self._connector_local_to_world(connector, lx, ly)
            result.append((label, wx, wy))
        return result

    def _all_pin_world_points(self) -> List[Tuple[str, str, float, float]]:
        """Alle pins van alle connectors als (connector_id, pin_label, x, y)."""
        pins: List[Tuple[str, str, float, float]] = []
        for connector in self.connectors:
            for label, wx, wy in self._connector_pin_world_points(connector):
                pins.append((connector.connector_id, label, wx, wy))
        return pins

    def _nearest_pin(self, pins: List[Tuple[str, str, float, float]], point: Tuple[float, float], tol_mm: float) -> Optional[Tuple[str, str]]:
        best: Optional[Tuple[str, str]] = None
        best_dist = tol_mm
        for connector_id, label, wx, wy in pins:
            dist = math.hypot(wx - point[0], wy - point[1])
            if dist <= best_dist:
                best_dist = dist
                best = (connector_id, label)
        return best

    def derive_netlist_from_geometry(self, tol_mm: float = 4.0, announce: bool = True) -> int:
        """Vul van/naar connector+pin van elke draad op basis van welke pin het
        draadeinde geometrisch raakt. Geeft het aantal bijgewerkte koppelingen terug."""
        pins = self._all_pin_world_points()
        if not pins:
            if announce:
                self._show_info("Geen connector-pins gevonden. Plaats eerst connectors met pins.")
            return 0
        before = self._capture_before_change()
        changed = 0
        for wire in self.wires:
            if len(wire.points_mm) < 2:
                continue
            start_pin = self._nearest_pin(pins, wire.points_mm[0], tol_mm)
            end_pin = self._nearest_pin(pins, wire.points_mm[-1], tol_mm)
            if start_pin and (wire.from_connector != start_pin[0] or wire.from_pin != start_pin[1]):
                wire.from_connector, wire.from_pin = start_pin
                changed += 1
            if end_pin and (wire.to_connector != end_pin[0] or wire.to_pin != end_pin[1]):
                wire.to_connector, wire.to_pin = end_pin
                changed += 1
        if changed:
            self.redraw()
            if self.selected and self.selected[0] == "wire":
                self.load_selection_properties_to_panel()
            self._commit_change(before, "Netlist uit tekening afgeleid.")
        if announce:
            self.status(f"Netlist afgeleid: {changed} koppeling(en) bijgewerkt op basis van pin-posities.")
        return changed

    def _table_col_widths(self, table: TableBox) -> List[float]:
        widths = [float(w) for w in table.col_widths_mm if float(w) > 0.0]
        if len(widths) != table.cols:
            widths = [max(1.0, float(table.cell_w_mm)) for _ in range(table.cols)]
            table.col_widths_mm = list(widths)
        return widths

    def _table_row_heights(self, table: TableBox) -> List[float]:
        heights = [float(h) for h in table.row_heights_mm if float(h) > 0.0]
        if len(heights) != table.rows:
            heights = [max(1.0, float(table.cell_h_mm)) for _ in range(table.rows)]
            table.row_heights_mm = list(heights)
        return heights

    def _table_size(self, table: TableBox) -> Tuple[float, float]:
        widths = self._table_col_widths(table)
        heights = self._table_row_heights(table)
        return (sum(widths), sum(heights))

    def _table_cell_from_point(self, table: TableBox, x_mm: float, y_mm: float) -> Optional[Tuple[int, int]]:
        widths = self._table_col_widths(table)
        heights = self._table_row_heights(table)
        total_w = sum(widths)
        total_h = sum(heights)
        if not (table.x_mm <= x_mm <= table.x_mm + total_w and table.y_mm <= y_mm <= table.y_mm + total_h):
            return None

        rx = x_mm - table.x_mm
        ry = y_mm - table.y_mm
        col = 0
        acc = 0.0
        for i, w in enumerate(widths):
            acc += w
            if rx <= acc or i == len(widths) - 1:
                col = i
                break
        row = 0
        acc = 0.0
        for i, h in enumerate(heights):
            acc += h
            if ry <= acc or i == len(heights) - 1:
                row = i
                break
        return (row, col)

    def _find_table_resize_handle(self, table: TableBox, x_mm: float, y_mm: float) -> Optional[Tuple[str, int]]:
        widths = self._table_col_widths(table)
        heights = self._table_row_heights(table)
        total_w = sum(widths)
        total_h = sum(heights)
        tol = max(0.8, 4.0 / self.zoom)

        if not (table.x_mm - tol <= x_mm <= table.x_mm + total_w + tol and table.y_mm - tol <= y_mm <= table.y_mm + total_h + tol):
            return None

        cx = table.x_mm
        for i in range(table.cols - 1):
            cx += widths[i]
            if abs(x_mm - cx) <= tol and table.y_mm <= y_mm <= table.y_mm + total_h:
                return ("col", i)

        cy = table.y_mm
        for i in range(table.rows - 1):
            cy += heights[i]
            if abs(y_mm - cy) <= tol and table.x_mm <= x_mm <= table.x_mm + total_w:
                return ("row", i)
        return None

    def _table_cell_rect(self, table: TableBox, row: int, col: int) -> Tuple[float, float, float, float]:
        widths = self._table_col_widths(table)
        heights = self._table_row_heights(table)
        x = table.x_mm + sum(widths[:col])
        y = table.y_mm + sum(heights[:row])
        return (x, y, widths[col], heights[row])

    def _table_text_anchor_position(self, table: TableBox, x: float, y: float, w: float, h: float) -> Tuple[float, float, str]:
        pad = 1.2
        if table.text_h_align == "left":
            tx = x + pad
            h_anchor = "w"
        elif table.text_h_align == "right":
            tx = x + w - pad
            h_anchor = "e"
        else:
            tx = x + w / 2.0
            h_anchor = ""

        if table.text_v_align == "top":
            ty = y + pad
            v_anchor = "n"
        elif table.text_v_align == "bottom":
            ty = y + h - pad
            v_anchor = "s"
        else:
            ty = y + h / 2.0
            v_anchor = ""

        anchor = (v_anchor + h_anchor) or "center"
        return (tx, ty, anchor)

    def _svg_anchor_attrs(self, anchor: str) -> Tuple[str, str]:
        if "w" in anchor:
            text_anchor = "start"
        elif "e" in anchor:
            text_anchor = "end"
        else:
            text_anchor = "middle"

        if "n" in anchor:
            baseline = "hanging"
        elif "s" in anchor:
            baseline = "text-after-edge"
        else:
            baseline = "middle"
        return (text_anchor, baseline)

    def place_connector_at(self, x_mm: float, y_mm: float):
        if not self.active_symbol or self.active_symbol not in self.symbols:
            self._show_info("Importeer eerst een STEP connector en selecteer die in de bibliotheek.")
            return
        sym = self.symbols[self.active_symbol]
        ref_default = self._next_id("J", [c.connector_id for c in self.connectors])
        ref = self._ask_string("Connector ID:", initialvalue=ref_default)
        if ref is None:
            return
        ref = ref.strip() or ref_default
        if any(c.connector_id == ref for c in self.connectors):
            self._show_error(f"Connector ID '{ref}' bestaat al.")
            return

        before = self._capture_before_change()
        max_side = max(sym.width_mm, sym.height_mm, 0.001)
        auto_scale = clamp(32.0 / max_side, 0.2, 3.0)
        connector = ConnectorInstance(
            connector_id=ref,
            symbol_name=self.active_symbol,
            x_mm=x_mm,
            y_mm=y_mm,
            scale=auto_scale,
            line_color=self.default_connector_line_color,
            line_width_mm=self.default_connector_line_width_mm,
        )
        self.connectors.append(connector)
        # Plaats de naam standaard net boven het symbool (los verplaatsbaar daarna).
        bx1, by1, bx2, by2 = self._connector_world_bbox(connector)
        connector.label_dx_mm = round((bx1 + bx2) / 2.0 - x_mm, 3)
        connector.label_dy_mm = round(by1 - y_mm - 3.0, 3)
        self._set_single_selection("connector", ref)
        self.load_selection_properties_to_panel()
        self.redraw()
        self._commit_change(before, f"Connector {ref} geplaatst.")

    def finish_wire(self):
        self.temp_wire_points = []
        self.temp_wire_anchor_meta = None
        self.temp_wire_segment_history = []
        self.status("Draadketen gestopt.")
        self.redraw()

    def add_leader(self, start: Tuple[float, float], end: Tuple[float, float]):
        leader_text = ""
        if self.mode == "draw_leader":
            leader_text = self.prop_text_var.get().strip()
            self._sync_leader_defaults_from_property_panel()
        before = self._capture_before_change()
        leader_id = self._next_id("L", [l.leader_id for l in self.leaders])
        self.leaders.append(
            Leader(
                leader_id=leader_id,
                start_mm=start,
                end_mm=end,
                text=leader_text,
                color=self.default_leader_color,
                width_mm=self.default_leader_width_mm,
                arrow_size_mm=self.default_leader_arrow_size_mm,
                text_size_pt=self.default_leader_text_size_pt,
                text_box=self.default_leader_text_box,
            )
        )
        self._set_single_selection("leader", leader_id)
        self.load_selection_properties_to_panel()
        self._commit_change(before)

    def add_dimension(self, p1: Tuple[float, float], p2: Tuple[float, float]):
        if math.dist(p1, p2) <= 1e-6:
            self.status("Maatlijn: meetpunten vallen samen, niets geplaatst.")
            return
        if self.mode == "draw_dimension":
            self._sync_dimension_defaults_from_property_panel()
        orientation = normalize_dimension_orientation(self.default_dimension_orientation)
        # Auto-pick horizontal/vertical from dominant axis unless 'aligned' is chosen.
        if orientation != "aligned":
            orientation = "horizontal" if abs(p2[0] - p1[0]) >= abs(p2[1] - p1[1]) else "vertical"
        before = self._capture_before_change()
        dim_id = self._next_id("D", [d.dim_id for d in self.dimensions])
        self.dimensions.append(
            DimensionLine(
                dim_id=dim_id,
                p1_mm=p1,
                p2_mm=p2,
                orientation=orientation,
                offset_mm=self.default_dimension_offset_mm,
                color=self.default_dimension_color,
                line_width_mm=self.default_dimension_line_width_mm,
                arrow_size_mm=self.default_dimension_arrow_size_mm,
                text_size_pt=self.default_dimension_text_size_pt,
                tolerance=self.default_dimension_tolerance,
            )
        )
        self._set_single_selection("dimension", dim_id)
        self.load_selection_properties_to_panel()
        self._commit_change(before, f"Maatlijn {dim_id} geplaatst.")

    def add_table_from_corners(self, a: Tuple[float, float], b: Tuple[float, float]):
        x1 = min(a[0], b[0])
        y1 = min(a[1], b[1])
        x2 = max(a[0], b[0])
        y2 = max(a[1], b[1])
        width = max(20.0, x2 - x1)
        height = max(10.0, y2 - y1)

        rows = self._ask_integer("Aantal rijen:", initialvalue=4, minvalue=1, maxvalue=40)
        cols = self._ask_integer("Aantal kolommen:", initialvalue=3, minvalue=1, maxvalue=20)
        if rows is None or cols is None:
            return

        before = self._capture_before_change()
        table_id = self._next_id("T", [t.table_id for t in self.tables])
        cell_w = width / cols
        cell_h = height / rows
        cells = [["" for _ in range(cols)] for _ in range(rows)]
        col_widths = [cell_w for _ in range(cols)]
        row_heights = [cell_h for _ in range(rows)]
        self.tables.append(
            TableBox(
                table_id=table_id,
                x_mm=x1,
                y_mm=y1,
                cols=cols,
                rows=rows,
                cell_w_mm=cell_w,
                cell_h_mm=cell_h,
                border_color=self.default_table_border_color,
                border_width_mm=self.default_table_border_width_mm,
                col_widths_mm=col_widths,
                row_heights_mm=row_heights,
                text_h_align="center",
                text_v_align="middle",
                is_border=False,
                cells=cells,
            )
        )
        self._set_single_selection("table", table_id)
        self.load_selection_properties_to_panel()
        self._commit_change(before)

    def edit_table_cell_at_cursor(self):
        x, y = self.cursor_world
        for table in reversed(self.tables):
            if table.is_border:
                continue
            cell = self._table_cell_from_point(table, x, y)
            if cell is None:
                continue
            row, col = cell
            current = table.cells[row][col] if row < len(table.cells) and col < len(table.cells[row]) else ""
            text = self._ask_string(f"Cel [{row + 1},{col + 1}] tekst:", initialvalue=current)
            if text is None:
                return True
            before = self._capture_before_change()
            table.cells[row][col] = text
            self._set_single_selection("table", table.table_id)
            self.load_selection_properties_to_panel()
            self.redraw()
            self._commit_change(before)
            return True
        return False

    def edit_selected_properties(self):
        self.focus_properties_panel()

    def _selected_center(self) -> Optional[Tuple[float, float]]:
        if not self.selected:
            return None
        kind, ident = self.selected
        if kind == "connector":
            obj = self._find_connector(ident)
            if obj:
                return (obj.x_mm, obj.y_mm)
        elif kind == "wire":
            obj = self._find_wire(ident)
            if obj and obj.points_mm:
                xs = [p[0] for p in obj.points_mm]
                ys = [p[1] for p in obj.points_mm]
                return ((min(xs) + max(xs)) / 2.0, (min(ys) + max(ys)) / 2.0)
        elif kind == "leader":
            obj = self._find_leader(ident)
            if obj:
                xs = [obj.start_mm[0], obj.end_mm[0]]
                ys = [obj.start_mm[1], obj.end_mm[1]]
                return ((min(xs) + max(xs)) / 2.0, (min(ys) + max(ys)) / 2.0)
        elif kind == "text":
            obj = self._find_text_note(ident)
            if obj:
                return (obj.x_mm, obj.y_mm)
        elif kind == "image":
            obj = self._find_image_note(ident)
            if obj:
                return (obj.x_mm + (obj.width_mm * obj.scale) / 2.0, obj.y_mm + (obj.height_mm * obj.scale) / 2.0)
        elif kind == "table":
            obj = self._find_table(ident)
            if obj:
                tw, th = self._table_size(obj)
                return (obj.x_mm + tw / 2.0, obj.y_mm + th / 2.0)
        return None

    def _rotate_point(self, point: Tuple[float, float], center: Tuple[float, float], angle_deg: float) -> Tuple[float, float]:
        x, y = point
        cx, cy = center
        angle = math.radians(angle_deg)
        ca = math.cos(angle)
        sa = math.sin(angle)
        dx = x - cx
        dy = y - cy
        return (cx + dx * ca - dy * sa, cy + dx * sa + dy * ca)

    def rotate_selected(self, angle_deg: float):
        if not self.selected:
            return
        center = self._selected_center()
        if not center:
            return
        before = self._capture_before_change()
        kind, ident = self.selected
        if kind == "connector":
            obj = self._find_connector(ident)
            if obj:
                obj.rotation_deg = (obj.rotation_deg + angle_deg) % 360.0
        elif kind == "wire":
            obj = self._find_wire(ident)
            if obj:
                obj.points_mm = [self._rotate_point(p, center, angle_deg) for p in obj.points_mm]
        elif kind == "leader":
            obj = self._find_leader(ident)
            if obj:
                obj.start_mm = self._rotate_point(obj.start_mm, center, angle_deg)
                obj.end_mm = self._rotate_point(obj.end_mm, center, angle_deg)
        elif kind == "table":
            self._show_info("Tabel roteren wordt nog niet ondersteund in deze versie.")
            return
        self.redraw()
        self._commit_change(before)

    def mirror_selected(self, axis: str):
        if not self.selected:
            return
        center = self._selected_center()
        if not center:
            return
        before = self._capture_before_change()
        cx, cy = center
        kind, ident = self.selected

        def mirror_point(p: Tuple[float, float]) -> Tuple[float, float]:
            if axis == "lr":
                return (2 * cx - p[0], p[1])
            return (p[0], 2 * cy - p[1])

        if kind == "connector":
            obj = self._find_connector(ident)
            if obj:
                if axis == "lr":
                    obj.mirror_x = not obj.mirror_x
                else:
                    obj.mirror_y = not obj.mirror_y
        elif kind == "wire":
            obj = self._find_wire(ident)
            if obj:
                obj.points_mm = [mirror_point(p) for p in obj.points_mm]
        elif kind == "leader":
            obj = self._find_leader(ident)
            if obj:
                obj.start_mm = mirror_point(obj.start_mm)
                obj.end_mm = mirror_point(obj.end_mm)
        elif kind == "table":
            obj = self._find_table(ident)
            if obj:
                if axis == "lr":
                    obj.cells = [list(reversed(row)) for row in obj.cells]
                    obj.col_widths_mm = list(reversed(self._table_col_widths(obj)))
                else:
                    obj.cells = list(reversed(obj.cells))
                    obj.row_heights_mm = list(reversed(self._table_row_heights(obj)))
        self.redraw()
        self._commit_change(before)

    def reset_selected_transform(self):
        if not self.selected:
            return
        before = self._capture_before_change()
        kind, ident = self.selected
        if kind == "connector":
            obj = self._find_connector(ident)
            if obj:
                obj.rotation_deg = 0.0
                obj.mirror_x = False
                obj.mirror_y = False
        self.redraw()
        self._commit_change(before)

    def set_selected_wire_style(self, style: str):
        if normalize_wire_style(style) != style:
            return
        items = self._active_selected_items()
        wire_ids = {ident for kind, ident in items if kind == "wire"}
        if not wire_ids and self.selected and self.selected[0] == "wire":
            wire_ids = {self.selected[1]}
        if not wire_ids:
            return
        before = self._capture_before_change()
        curve_styles = {"curve", "twisted_pair_curve"}
        smooth_candidates: set[str] = set()
        should_smooth = False
        for wire_id in sorted(wire_ids):
            wire = self._find_wire(wire_id)
            if not wire:
                continue
            previous_style = normalize_wire_style(wire.style)
            wire.style = normalize_wire_style(style)
            if wire.style in {"straight", "twisted_pair"}:
                wire.start_handle_offset_mm = (0.0, 0.0)
                wire.end_handle_offset_mm = (0.0, 0.0)
            elif wire.style in curve_styles:
                smooth_candidates.add(wire.wire_id)
                if previous_style not in curve_styles:
                    should_smooth = True
        if should_smooth and len(smooth_candidates) > 1:
            self._smooth_wire_chains_for_style(smooth_candidates)
        self._clear_wire_caches()
        self.load_selection_properties_to_panel()
        self.redraw()
        self._commit_change(before)

    def adjust_selected_wire_curve(self, delta_mm: float):
        if not self.selected or self.selected[0] != "wire":
            return
        wire = self._find_wire(self.selected[1])
        if not wire:
            return
        before = self._capture_before_change()
        wire.curve_offset_mm += float(delta_mm)
        wire.start_handle_offset_mm = (0.0, 0.0)
        wire.end_handle_offset_mm = (0.0, 0.0)
        if wire.style == "straight":
            wire.style = "curve"
        elif wire.style == "twisted_pair":
            wire.style = "twisted_pair_curve"
        self.load_selection_properties_to_panel()
        self.redraw()
        self._commit_change(before)

    def flip_selected_wire_curve(self):
        if not self.selected or self.selected[0] != "wire":
            return
        wire = self._find_wire(self.selected[1])
        if not wire:
            return
        before = self._capture_before_change()
        wire.curve_offset_mm = -wire.curve_offset_mm
        wire.start_handle_offset_mm = (0.0, 0.0)
        wire.end_handle_offset_mm = (0.0, 0.0)
        if wire.style == "straight":
            wire.style = "curve"
        elif wire.style == "twisted_pair":
            wire.style = "twisted_pair_curve"
        self.load_selection_properties_to_panel()
        self.redraw()
        self._commit_change(before)

    def prompt_selected_wire_curve_value(self):
        if not self.selected or self.selected[0] != "wire":
            return
        wire = self._find_wire(self.selected[1])
        if not self._wire_has_curve_handle(wire):
            self.status("Bochtwaarde invoeren werkt alleen voor een gebogen draad.")
            return
        value = self._ask_float(
            "Voer de bocht in millimeter in.\nPositief en negatief wisselen de bochtrichting.",
            initialvalue=wire.curve_offset_mm if wire else 0.0,
        )
        if value is None or not wire:
            return
        before = self._capture_before_change()
        wire.curve_offset_mm = round(float(value), 3)
        wire.start_handle_offset_mm = (0.0, 0.0)
        wire.end_handle_offset_mm = (0.0, 0.0)
        self.prop_curve_var.set(f"{wire.curve_offset_mm:g}")
        self._clear_wire_geometry_caches()
        self.request_redraw()
        self._commit_change(before, f"Bocht van {wire.wire_id} aangepast.")

    def reset_selected_wire_tangent_handles(self):
        if not self.selected or self.selected[0] != "wire":
            return
        wire = self._find_wire(self.selected[1])
        if not wire:
            return
        before = self._capture_before_change()
        wire.start_handle_offset_mm = (0.0, 0.0)
        wire.end_handle_offset_mm = (0.0, 0.0)
        self._clear_wire_geometry_caches()
        self.request_redraw()
        self._commit_change(before, f"Tangenthandles van {wire.wire_id} gereset.")

    def offset_selected_wire_parallel(self):
        if not self.selected or self.selected[0] != "wire":
            return
        base_wire = self._find_wire(self.selected[1])
        if not base_wire:
            return
        endpoints = self._wire_endpoints(base_wire)
        if endpoints is None:
            return
        spacing = self._ask_float(
            "Offsetafstand in millimeter per parallelle draad:",
            initialvalue=3.0,
        )
        if spacing is None or abs(spacing) < 1e-6:
            return
        count = self._ask_integer(
            "Hoeveel extra parallelle draden wil je maken?",
            initialvalue=1,
            minvalue=1,
            maxvalue=64,
        )
        if count is None or count < 1:
            return

        dx = endpoints[1][0] - endpoints[0][0]
        dy = endpoints[1][1] - endpoints[0][1]
        length = math.hypot(dx, dy)
        if length < 1e-6:
            self.status("Parallelle offset kan niet op een nuldraad.")
            return
        nx = -dy / length
        ny = dx / length

        source_ids = self._selected_wire_ids()
        source_wires = [self._find_wire(wire_id) for wire_id in source_ids]
        source_wires = [wire for wire in source_wires if wire]
        if not source_wires:
            return

        before = self._capture_before_change()
        existing_ids = [wire.wire_id for wire in self.wires]
        last_created_id = None
        for offset_index in range(1, count + 1):
            delta_x = nx * spacing * offset_index
            delta_y = ny * spacing * offset_index
            for source_wire in source_wires:
                new_id = self._next_id("W", existing_ids)
                existing_ids.append(new_id)
                self.wires.append(
                    WirePath(
                        wire_id=new_id,
                        points_mm=[(x + delta_x, y + delta_y) for x, y in source_wire.points_mm],
                        color=source_wire.color,
                        color_b=source_wire.color_b,
                        width_mm=source_wire.width_mm,
                        style=source_wire.style,
                        curve_offset_mm=source_wire.curve_offset_mm,
                        start_handle_offset_mm=source_wire.start_handle_offset_mm,
                        end_handle_offset_mm=source_wire.end_handle_offset_mm,
                        twist_pitch_mm=source_wire.twist_pitch_mm,
                        pair_gap_mm=source_wire.pair_gap_mm,
                        label=source_wire.label,
                        **wire_electrical_kwargs(source_wire),
                    )
                )
                last_created_id = new_id

        if last_created_id:
            self._set_primary_wire_selection(last_created_id)
            self.load_selection_properties_to_panel()
        self.request_redraw()
        self._commit_change(before, f"{count} parallelle offset(s) aangemaakt.")

    def duplicate_selected(self):
        if not self.selected:
            return
        before = self._capture_before_change()
        kind, ident = self.selected
        dx = 12.0
        dy = 12.0
        if kind == "connector":
            obj = self._find_connector(ident)
            if not obj:
                return
            nid = self._next_id("J", [c.connector_id for c in self.connectors])
            clone = ConnectorInstance(
                connector_id=nid,
                symbol_name=obj.symbol_name,
                x_mm=obj.x_mm + dx,
                y_mm=obj.y_mm + dy,
                scale=obj.scale,
                rotation_deg=obj.rotation_deg,
                mirror_x=obj.mirror_x,
                mirror_y=obj.mirror_y,
                line_color=obj.line_color,
                line_width_mm=obj.line_width_mm,
                note=obj.note,
                part_number=obj.part_number,
                pin_count=obj.pin_count,
                pin_labels=list(obj.pin_labels),
                label_dx_mm=obj.label_dx_mm,
                label_dy_mm=obj.label_dy_mm,
                pin_offsets_mm=list(obj.pin_offsets_mm),
            )
            self.connectors.append(clone)
            self._set_single_selection("connector", nid)
        elif kind == "wire":
            obj = self._find_wire(ident)
            if not obj:
                return
            nid = self._next_id("W", [w.wire_id for w in self.wires])
            clone_pts = [(p[0] + dx, p[1] + dy) for p in obj.points_mm]
            self.wires.append(
                WirePath(
                    wire_id=nid,
                    points_mm=clone_pts,
                    color=obj.color,
                    color_b=obj.color_b,
                    width_mm=obj.width_mm,
                    style=obj.style,
                    curve_offset_mm=obj.curve_offset_mm,
                    start_handle_offset_mm=obj.start_handle_offset_mm,
                    end_handle_offset_mm=obj.end_handle_offset_mm,
                    twist_pitch_mm=obj.twist_pitch_mm,
                    pair_gap_mm=obj.pair_gap_mm,
                    label=obj.label,
                    **wire_electrical_kwargs(obj),
                )
            )
            self._set_primary_wire_selection(nid)
        elif kind == "leader":
            obj = self._find_leader(ident)
            if not obj:
                return
            nid = self._next_id("L", [l.leader_id for l in self.leaders])
            self.leaders.append(
                Leader(
                    leader_id=nid,
                    start_mm=(obj.start_mm[0] + dx, obj.start_mm[1] + dy),
                    end_mm=(obj.end_mm[0] + dx, obj.end_mm[1] + dy),
                    text=obj.text,
                    color=obj.color,
                    width_mm=obj.width_mm,
                    arrow_size_mm=obj.arrow_size_mm,
                    text_size_pt=obj.text_size_pt,
                    text_box=obj.text_box,
                )
            )
            self._set_single_selection("leader", nid)
        elif kind == "dimension":
            obj = self._find_dimension(ident)
            if not obj:
                return
            nid = self._next_id("D", [d.dim_id for d in self.dimensions])
            self.dimensions.append(
                DimensionLine(
                    dim_id=nid,
                    p1_mm=(obj.p1_mm[0] + dx, obj.p1_mm[1] + dy),
                    p2_mm=(obj.p2_mm[0] + dx, obj.p2_mm[1] + dy),
                    orientation=obj.orientation,
                    offset_mm=obj.offset_mm,
                    color=obj.color,
                    line_width_mm=obj.line_width_mm,
                    arrow_size_mm=obj.arrow_size_mm,
                    text_size_pt=obj.text_size_pt,
                    tolerance=obj.tolerance,
                    override_text=obj.override_text,
                    decimals=obj.decimals,
                )
            )
            self._set_single_selection("dimension", nid)
        elif kind == "table":
            obj = self._find_table(ident)
            if not obj:
                return
            nid = self._next_id("T", [t.table_id for t in self.tables])
            self.tables.append(
                TableBox(
                    table_id=nid,
                    x_mm=obj.x_mm + dx,
                    y_mm=obj.y_mm + dy,
                    cols=obj.cols,
                    rows=obj.rows,
                    cell_w_mm=obj.cell_w_mm,
                    cell_h_mm=obj.cell_h_mm,
                    border_color=obj.border_color,
                    border_width_mm=obj.border_width_mm,
                    col_widths_mm=list(self._table_col_widths(obj)),
                    row_heights_mm=list(self._table_row_heights(obj)),
                    text_h_align=obj.text_h_align,
                    text_v_align=obj.text_v_align,
                    is_border=obj.is_border,
                    cells=[list(r) for r in obj.cells],
                )
            )
            self._set_single_selection("table", nid)
        elif kind == "text":
            obj = self._find_text_note(ident)
            if not obj:
                return
            nid = self._next_id("N", [n.note_id for n in self.text_notes])
            self.text_notes.append(
                TextNote(
                    note_id=nid,
                    x_mm=obj.x_mm + dx,
                    y_mm=obj.y_mm + dy,
                    text=obj.text,
                    color=obj.color,
                    font_size_pt=obj.font_size_pt,
                )
            )
            self._set_single_selection("text", nid)
        elif kind == "image":
            obj = self._find_image_note(ident)
            if not obj:
                return
            nid = self._next_id("I", [n.image_id for n in self.image_notes])
            self.image_notes.append(
                ImageNote(
                    image_id=nid,
                    x_mm=obj.x_mm + dx,
                    y_mm=obj.y_mm + dy,
                    width_mm=obj.width_mm,
                    height_mm=obj.height_mm,
                    scale=obj.scale,
                    source_path=obj.source_path,
                    image_data_b64=obj.image_data_b64,
                    mime_type=obj.mime_type,
                )
            )
            self._set_single_selection("image", nid)

        self.load_selection_properties_to_panel()
        self.redraw()
        self._commit_change(before)

    def bring_selected_to_front(self):
        if not self.selected:
            return
        before = self._capture_before_change()
        kind, ident = self.selected
        if kind == "connector":
            for i, obj in enumerate(self.connectors):
                if obj.connector_id == ident:
                    self.connectors.append(self.connectors.pop(i))
                    break
        elif kind == "wire":
            for i, obj in enumerate(self.wires):
                if obj.wire_id == ident:
                    self.wires.append(self.wires.pop(i))
                    break
        elif kind == "leader":
            for i, obj in enumerate(self.leaders):
                if obj.leader_id == ident:
                    self.leaders.append(self.leaders.pop(i))
                    break
        elif kind == "table":
            for i, obj in enumerate(self.tables):
                if obj.table_id == ident:
                    self.tables.append(self.tables.pop(i))
                    break
        elif kind == "text":
            for i, obj in enumerate(self.text_notes):
                if obj.note_id == ident:
                    self.text_notes.append(self.text_notes.pop(i))
                    break
        elif kind == "image":
            for i, obj in enumerate(self.image_notes):
                if obj.image_id == ident:
                    self.image_notes.append(self.image_notes.pop(i))
                    break
        self.redraw()
        self._commit_change(before)

    def send_selected_to_back(self):
        if not self.selected:
            return
        before = self._capture_before_change()
        kind, ident = self.selected
        if kind == "connector":
            for i, obj in enumerate(self.connectors):
                if obj.connector_id == ident:
                    self.connectors.insert(0, self.connectors.pop(i))
                    break
        elif kind == "wire":
            for i, obj in enumerate(self.wires):
                if obj.wire_id == ident:
                    self.wires.insert(0, self.wires.pop(i))
                    break
        elif kind == "leader":
            for i, obj in enumerate(self.leaders):
                if obj.leader_id == ident:
                    self.leaders.insert(0, self.leaders.pop(i))
                    break
        elif kind == "table":
            for i, obj in enumerate(self.tables):
                if obj.table_id == ident:
                    self.tables.insert(0, self.tables.pop(i))
                    break
        elif kind == "text":
            for i, obj in enumerate(self.text_notes):
                if obj.note_id == ident:
                    self.text_notes.insert(0, self.text_notes.pop(i))
                    break
        elif kind == "image":
            for i, obj in enumerate(self.image_notes):
                if obj.image_id == ident:
                    self.image_notes.insert(0, self.image_notes.pop(i))
                    break
        self.redraw()
        self._commit_change(before)

    def table_insert_row(self, table_id: str, row_index: int, below: bool = True):
        table = self._find_table(table_id)
        if not table:
            return
        before = self._capture_before_change()
        row_index = max(0, min(table.rows - 1, row_index))
        insert_at = row_index + 1 if below else row_index
        widths = self._table_col_widths(table)
        heights = self._table_row_heights(table)
        ref_h = heights[row_index]
        table.cells.insert(insert_at, ["" for _ in range(table.cols)])
        heights.insert(insert_at, ref_h)
        table.rows += 1
        table.row_heights_mm = heights
        table.col_widths_mm = widths
        table.cell_h_mm = sum(heights) / max(1, len(heights))
        self.redraw()
        self._commit_change(before)

    def table_insert_col(self, table_id: str, col_index: int, right: bool = True):
        table = self._find_table(table_id)
        if not table:
            return
        before = self._capture_before_change()
        col_index = max(0, min(table.cols - 1, col_index))
        insert_at = col_index + 1 if right else col_index
        widths = self._table_col_widths(table)
        heights = self._table_row_heights(table)
        ref_w = widths[col_index]
        for r in range(table.rows):
            table.cells[r].insert(insert_at, "")
        widths.insert(insert_at, ref_w)
        table.cols += 1
        table.col_widths_mm = widths
        table.row_heights_mm = heights
        table.cell_w_mm = sum(widths) / max(1, len(widths))
        self.redraw()
        self._commit_change(before)

    def table_remove_row(self, table_id: str, row_index: int):
        table = self._find_table(table_id)
        if not table or table.rows <= 1:
            return
        before = self._capture_before_change()
        row_index = max(0, min(table.rows - 1, row_index))
        heights = self._table_row_heights(table)
        del table.cells[row_index]
        del heights[row_index]
        table.rows -= 1
        table.row_heights_mm = heights
        table.cell_h_mm = sum(heights) / max(1, len(heights))
        self.redraw()
        self._commit_change(before)

    def table_remove_col(self, table_id: str, col_index: int):
        table = self._find_table(table_id)
        if not table or table.cols <= 1:
            return
        before = self._capture_before_change()
        col_index = max(0, min(table.cols - 1, col_index))
        widths = self._table_col_widths(table)
        for r in range(table.rows):
            del table.cells[r][col_index]
        del widths[col_index]
        table.cols -= 1
        table.col_widths_mm = widths
        table.cell_w_mm = sum(widths) / max(1, len(widths))
        self.redraw()
        self._commit_change(before)

    def table_set_text_align(self, table_id: str, h_align: Optional[str] = None, v_align: Optional[str] = None):
        table = self._find_table(table_id)
        if not table:
            return
        before = self._capture_before_change()
        if h_align in {"left", "center", "right"}:
            table.text_h_align = h_align
        if v_align in {"top", "middle", "bottom"}:
            table.text_v_align = v_align
        self.redraw()
        self._commit_change(before)

    def delete_selected(self):
        items = self._active_selected_items()
        if not items:
            return
        before = self._capture_before_change()
        connector_ids = {ident for kind, ident in items if kind == "connector"}
        wire_ids = {ident for kind, ident in items if kind == "wire"}
        leader_ids = {ident for kind, ident in items if kind == "leader"}
        dimension_ids = {ident for kind, ident in items if kind == "dimension"}
        table_ids = {ident for kind, ident in items if kind == "table"}
        text_ids = {ident for kind, ident in items if kind == "text"}
        image_ids = {ident for kind, ident in items if kind == "image"}
        if connector_ids:
            self.connectors = [c for c in self.connectors if c.connector_id not in connector_ids]
            self._clear_connector_caches()
        if wire_ids:
            delete_ids = self._valid_selected_wire_ids() or wire_ids
            self.wires = [w for w in self.wires if w.wire_id not in delete_ids]
            self._clear_wire_caches()
        if leader_ids:
            self.leaders = [l for l in self.leaders if l.leader_id not in leader_ids]
        if dimension_ids:
            self.dimensions = [d for d in self.dimensions if d.dim_id not in dimension_ids]
        if table_ids:
            self.tables = [t for t in self.tables if t.table_id not in table_ids]
        if text_ids:
            self.text_notes = [n for n in self.text_notes if n.note_id not in text_ids]
        if image_ids:
            self.image_notes = [n for n in self.image_notes if n.image_id not in image_ids]
            for image_id in image_ids:
                self._image_canvas_cache.pop(image_id, None)
        self._clear_selection()
        self.load_selection_properties_to_panel()
        self.redraw()
        self._commit_change(before)

    def _frame_bounds(self) -> Tuple[float, float, float, float]:
        f = self.frame_margin_mm
        return (f, f, self.paper_w_mm - f, self.paper_h_mm - f)

    def _bbox_inside_frame(self, bbox: Tuple[float, float, float, float]) -> bool:
        xmin, ymin, xmax, ymax = self._frame_bounds()
        bx1, by1, bx2, by2 = bbox
        return (xmin <= bx1 <= xmax and xmin <= bx2 <= xmax and ymin <= by1 <= ymax and ymin <= by2 <= ymax)

    def _wire_total_length(self, wire: WirePath) -> float:
        polylines = self._wire_display_polylines(wire)
        total = 0.0
        for line in polylines:
            for i in range(len(line) - 1):
                total += math.dist(line[i], line[i + 1])
        return total

    def _wire_supports_bridge(self, wire: WirePath) -> bool:
        return normalize_wire_style(wire.style) in {"straight", "curve"}

    def _wire_bridge_specs(self) -> Dict[str, List[dict]]:
        dragging_wires = bool(
            self.drag_start_world is not None
            and (
                (self.selected and self.selected[0] == "wire")
                or (self.drag_original_items and any(kind == "wire" for kind, _ident in self.drag_original_items))
            )
        )
        if self.mode == "draw_wire" or dragging_wires:
            return {}

        bridge_enabled, bridge_height_mm, bridge_length_mm, bridge_clearance_mm = self._wire_bridge_settings_signature()
        if not bridge_enabled:
            return {}

        signature = (("settings", bridge_enabled, bridge_height_mm, bridge_length_mm, bridge_clearance_mm),) + tuple(
            (
                idx,
                self._wire_geometry_signature(wire),
                round(wire.width_mm, 4),
            )
            for idx, wire in enumerate(self.wires)
        )
        if signature == self._wire_bridge_signature:
            return self._wire_bridge_cache

        bridge_specs: Dict[str, List[dict]] = {}
        centerlines = {w.wire_id: self._wire_centerline_points(w, curve_samples=60) for w in self.wires}
        prefix_lengths: Dict[str, List[float]] = {}
        for wire in self.wires:
            line = centerlines.get(wire.wire_id, [])
            lengths = [0.0]
            for idx in range(len(line) - 1):
                lengths.append(lengths[-1] + math.dist(line[idx], line[idx + 1]))
            prefix_lengths[wire.wire_id] = lengths

        # Broad-phase: bounding box per draad, zodat draadparen die elkaar onmogelijk kunnen
        # kruisen meteen overgeslagen worden. Dat haalt de kruisingsdetectie van O(n²) effectief
        # naar ~O(n) voor een normale (gebundelde) kabelboom waar de meeste draden niet overlappen.
        bboxes: Dict[str, Tuple[float, float, float, float]] = {}
        for wid, line in centerlines.items():
            if len(line) < 2:
                continue
            xs = [p[0] for p in line]
            ys = [p[1] for p in line]
            bboxes[wid] = (min(xs), min(ys), max(xs), max(ys))

        for lower_idx, lower_wire in enumerate(self.wires):
            lower_line = centerlines.get(lower_wire.wire_id, [])
            if len(lower_line) < 2:
                continue
            lower_bbox = bboxes.get(lower_wire.wire_id)
            for top_idx in range(lower_idx + 1, len(self.wires)):
                top_wire = self.wires[top_idx]
                if not self._wire_supports_bridge(top_wire):
                    continue
                top_line = centerlines.get(top_wire.wire_id, [])
                if len(top_line) < 2:
                    continue
                top_bbox = bboxes.get(top_wire.wire_id)
                if lower_bbox and top_bbox and (
                    top_bbox[0] > lower_bbox[2]
                    or top_bbox[2] < lower_bbox[0]
                    or top_bbox[1] > lower_bbox[3]
                    or top_bbox[3] < lower_bbox[1]
                ):
                    continue

                top_segments = max(1, len(top_line) - 1)
                lower_segments = max(1, len(lower_line) - 1)
                wire_specs = bridge_specs.setdefault(top_wire.wire_id, [])
                for lower_seg_idx in range(lower_segments):
                    lower_a = lower_line[lower_seg_idx]
                    lower_b = lower_line[lower_seg_idx + 1]
                    for top_seg_idx in range(top_segments):
                        top_a = top_line[top_seg_idx]
                        top_b = top_line[top_seg_idx + 1]
                        hit = segment_intersection(top_a, top_b, lower_a, lower_b)
                        if not hit:
                            continue
                        point, top_local_t, lower_local_t = hit
                        top_param = (top_seg_idx + top_local_t) / top_segments
                        lower_param = (lower_seg_idx + lower_local_t) / lower_segments
                        if top_param <= 0.05 or top_param >= 0.95 or lower_param <= 0.05 or lower_param >= 0.95:
                            continue
                        endpoint_tol = max(0.8, max(top_wire.width_mm, lower_wire.width_mm) * 0.9)
                        if any(math.dist(point, end) <= endpoint_tol for end in top_wire.points_mm):
                            continue
                        if any(math.dist(point, end) <= endpoint_tol for end in lower_wire.points_mm):
                            continue
                        dx = top_b[0] - top_a[0]
                        dy = top_b[1] - top_a[1]
                        seg_len = math.hypot(dx, dy)
                        if seg_len < 1e-6:
                            continue
                        center_dist = prefix_lengths.get(top_wire.wire_id, [0.0])[top_seg_idx] + top_local_t * seg_len
                        tx = dx / seg_len
                        ty = dy / seg_len
                        nx = -ty
                        ny = tx
                        if ny > 0 or (abs(ny) < 1e-9 and nx < 0):
                            nx = -nx
                            ny = -ny
                        arc_half_len = max(0.25, bridge_length_mm / 2.0)
                        clear_half_len = max(arc_half_len, bridge_clearance_mm / 2.0)
                        height = max(0.1, bridge_height_mm)
                        if any(abs(center_dist - spec["distance"]) <= clear_half_len * 1.4 for spec in wire_specs):
                            continue
                        wire_specs.append(
                            {
                                "point": point,
                                "distance": center_dist,
                                "clear_start": max(0.0, center_dist - clear_half_len),
                                "clear_end": center_dist + clear_half_len,
                                "arc_start": max(0.0, center_dist - arc_half_len),
                                "arc_end": center_dist + arc_half_len,
                                "normal": (nx, ny),
                                "height": height,
                            }
                        )
        for specs in bridge_specs.values():
            specs.sort(key=lambda spec: spec["distance"])
        self._wire_bridge_signature = signature
        self._wire_bridge_cache = bridge_specs
        return bridge_specs

    def show_project_inventory(self):
        styles = {"straight": 0, "curve": 0, "twisted_pair": 0, "twisted_pair_curve": 0}
        total_wire_mm = 0.0
        border_count = sum(1 for t in self.tables if t.is_border)
        table_count = sum(1 for t in self.tables if not t.is_border)
        electrical_count = 0
        electrical_length_mm = 0.0
        for w in self.wires:
            styles[normalize_wire_style(w.style)] += 1
            total_wire_mm += self._wire_total_length(w)
            if any([w.signal_name, w.from_connector, w.from_pin, w.to_connector, w.to_pin, w.net_name]):
                electrical_count += 1
            electrical_length_mm += max(0.0, w.length_mm)
        report = [
            f"Project: {self.project_name_var.get().strip() or '-'}",
            f"Revisie: {self.rev_var.get().strip() or '-'}",
            f"Engineer: {self.engineer_var.get().strip() or '-'}",
            f"Bladformaat: {paper_preset_for_dimensions(self.paper_w_mm, self.paper_h_mm)} ({self.paper_w_mm:.0f} x {self.paper_h_mm:.0f} mm)",
            "",
            f"Connector symbolen: {len(self.symbols)}",
            f"Connector instanties: {len(self.connectors)}",
            f"Draden (segmenten): {len(self.wires)}",
            f"  - Recht: {styles['straight']}",
            f"  - Gebogen: {styles['curve']}",
            f"  - Twisted pair: {styles['twisted_pair']}",
            f"  - Twisted pair gebogen: {styles['twisted_pair_curve']}",
            f"Leaders: {len(self.leaders)}",
            f"Tabellen: {table_count}",
            "",
            f"Totale draadlengte (geschat): {total_wire_mm:.1f} mm ({total_wire_mm / 1000.0:.3f} m)",
            f"Elektrisch ingevulde draden: {electrical_count}/{len(self.wires)}",
            f"Elektrische lengte (BOM): {electrical_length_mm:.1f} mm ({electrical_length_mm / 1000.0:.3f} m)",
            (
                f"Kruising-boogjes: {'aan' if self.wire_bridge_enabled else 'uit'} "
                f"(hoogte {self.wire_bridge_height_mm:g}, lengte {self.wire_bridge_length_mm:g}, vrijruimte {self.wire_bridge_clearance_mm:g} mm)"
            ),
            f"Snap: grid={'aan' if self.snap_grid_enabled_var.get() else 'uit'} ({self._grid_step_mm():g} mm), endpoint={'aan' if self.snap_endpoint_enabled_var.get() else 'uit'}",
        ]
        if border_count:
            report.insert(-2, f"Legacy borders: {border_count}")
        self._show_info("\n".join(report))

    def run_project_check(self):
        findings: List[str] = []
        warns: List[str] = []

        def check_duplicate(ids: List[str], label: str):
            seen = set()
            dup = set()
            for item in ids:
                if item in seen:
                    dup.add(item)
                seen.add(item)
            for d in sorted(dup):
                findings.append(f"[FOUT] Dubbele {label}-ID: {d}")

        check_duplicate([c.connector_id for c in self.connectors], "connector")
        check_duplicate([w.wire_id for w in self.wires], "wire")
        check_duplicate([l.leader_id for l in self.leaders], "leader")
        check_duplicate([t.table_id for t in self.tables], "tabel")

        connectors_by_id = {c.connector_id: c for c in self.connectors}
        connector_pin_data = {c.connector_id: (max(1, c.pin_count), list(c.pin_labels)) for c in self.connectors}

        for c in self.connectors:
            if not self._bbox_inside_frame(self._connector_world_bbox(c)):
                findings.append(f"[FOUT] Connector {c.connector_id} valt (deels) buiten het tekenframe.")
            if not c.part_number:
                warns.append(f"[WAARSCHUWING] Connector {c.connector_id} mist part number.")
            if c.pin_count < 1:
                findings.append(f"[FOUT] Connector {c.connector_id} heeft geen geldige pin-count.")
            if c.pin_labels and len(set(c.pin_labels)) != len(c.pin_labels):
                warns.append(f"[WAARSCHUWING] Connector {c.connector_id} heeft dubbele pinlabels.")

        for w in self.wires:
            polylines = self._wire_display_polylines(w)
            if not polylines:
                findings.append(f"[FOUT] Draad {w.wire_id} heeft geen geldige geometrie.")
                continue
            if self._wire_total_length(w) < 0.5:
                warns.append(f"[WAARSCHUWING] Draad {w.wire_id} is korter dan 0.5 mm.")
            bx1, by1, bx2, by2 = polyline_bbox(polylines)
            if not self._bbox_inside_frame((bx1, by1, bx2, by2)):
                warns.append(f"[WAARSCHUWING] Draad {w.wire_id} valt (deels) buiten het tekenframe.")

        electrical_findings, electrical_warns = wire_electrical_drc(self.wires, connector_pin_data)
        findings.extend(electrical_findings)
        warns.extend(electrical_warns)

        for l in self.leaders:
            if not self._bbox_inside_frame(self._leader_world_bbox(l)):
                warns.append(f"[WAARSCHUWING] Leader {l.leader_id} valt (deels) buiten het tekenframe.")

        for t in self.tables:
            tw, th = self._table_size(t)
            if not self._bbox_inside_frame((t.x_mm, t.y_mm, t.x_mm + tw, t.y_mm + th)):
                warns.append(f"[WAARSCHUWING] Tabel {t.table_id} valt (deels) buiten het tekenframe.")
            if len(t.cells) != t.rows or any(len(r) != t.cols for r in t.cells):
                findings.append(f"[FOUT] Tabel {t.table_id} heeft inconsistente celmatrix.")

        if not findings and not warns:
            self.status("Projectcontrole: geen issues gevonden.")
            self._show_info("Projectcontrole voltooid:\nGeen fouten of waarschuwingen gevonden.")
            return

        lines = ["Projectcontrole rapport", ""] + findings + warns
        self.status(f"Projectcontrole: {len(findings)} fouten, {len(warns)} waarschuwingen.")
        self._show_warning("\n".join(lines))

    def hit_test(self, x_mm: float, y_mm: float) -> Optional[Tuple[str, str]]:
        tol = max(1.5, 4.0 / self.zoom)
        for note in reversed(self.text_notes):
            bx1, by1, bx2, by2 = self._text_note_bbox(note)
            if bx1 - tol <= x_mm <= bx2 + tol and by1 - tol <= y_mm <= by2 + tol:
                return ("text", note.note_id)

        for t in reversed(self.tables):
            tw, th = self._table_size(t)
            if t.x_mm <= x_mm <= t.x_mm + tw and t.y_mm <= y_mm <= t.y_mm + th:
                return ("table", t.table_id)

        for l in reversed(self.leaders):
            points = self._leader_polyline(l)
            if any(
                distance_point_segment(x_mm, y_mm, points[idx][0], points[idx][1], points[idx + 1][0], points[idx + 1][1]) <= tol
                for idx in range(len(points) - 1)
            ):
                return ("leader", l.leader_id)
            label_bbox = self._leader_text_bbox(l, include_empty=self._is_item_selected("leader", l.leader_id))
            if label_bbox and label_bbox[0] - tol <= x_mm <= label_bbox[2] + tol and label_bbox[1] - tol <= y_mm <= label_bbox[3] + tol:
                return ("leader", l.leader_id)

        for dim in reversed(self.dimensions):
            geo = self._dimension_geometry(dim)
            f1, f2 = geo["feet"]
            segments = [(f1, f2), geo["ext"][0], geo["ext"][1]]
            if any(distance_point_segment(x_mm, y_mm, a[0], a[1], b[0], b[1]) <= tol for a, b in segments):
                return ("dimension", dim.dim_id)
            tx, ty = geo["text_pos"]
            half = max(6.0, geo["font_mm"] * len(geo["text"]) * 0.32)
            if tx - half - tol <= x_mm <= tx + half + tol and ty - geo["font_mm"] - tol <= y_mm <= ty + geo["font_mm"] + tol:
                return ("dimension", dim.dim_id)

        self._ensure_wire_segment_indexes()
        best_wire_id = None
        best_order = -1
        best_dist = float("inf")
        for entry in self._query_segment_index(self._wire_hit_segment_index, (x_mm, y_mm), tol + 4.0):
            wire_tol = tol + entry.get("extra_tol", 0.0)
            dist = distance_point_segment(x_mm, y_mm, entry["a"][0], entry["a"][1], entry["b"][0], entry["b"][1])
            if dist > wire_tol:
                continue
            if entry["wire_order"] > best_order or (entry["wire_order"] == best_order and dist < best_dist):
                best_order = entry["wire_order"]
                best_dist = dist
                best_wire_id = entry["wire_id"]
        if best_wire_id:
            return ("wire", best_wire_id)

        for c in reversed(self.connectors):
            sym = self.symbols.get(c.symbol_name)
            if not sym:
                continue
            bx1, by1, bx2, by2 = self._connector_world_bbox(c)
            if bx1 <= x_mm <= bx2 and by1 <= y_mm <= by2:
                return ("connector", c.connector_id)

        for note in reversed(self.image_notes):
            bx1, by1, bx2, by2 = self._image_note_bbox(note)
            if bx1 - tol <= x_mm <= bx2 + tol and by1 - tol <= y_mm <= by2 + tol:
                return ("image", note.image_id)
        return None

    # ---------------- Drawing ----------------


def main():
    configure_logging()
    enable_dpi_awareness()
    app = HarnessDrawingStudio()
    app.mainloop()


if __name__ == "__main__":
    main()
