"""Project-persistentie en data-export voor de tekenstudio.

ProjectIOMixin bevat het serialiseren/deserialiseren van een project (JSON) en
de exports (SVG/netlist/BOM). De hoofdklasse erft ervan; verwijzingen naar self
lossen op tegen die instantie. Bevat geen state of __init__.
"""

from __future__ import annotations

import json
from dataclasses import asdict
from pathlib import Path

from project_io import write_text_atomic
from geometry import clamp
from model import (
    BOM_CSV_HEADER,
    ConnectorInstance,
    DEFAULT_PAPER_PRESET,
    DEFAULT_WIRE_BRIDGE_CLEARANCE_MM,
    DEFAULT_WIRE_BRIDGE_ENABLED,
    DEFAULT_WIRE_BRIDGE_HEIGHT_MM,
    DEFAULT_WIRE_BRIDGE_LENGTH_MM,
    DimensionLine,
    ImageNote,
    Leader,
    NETLIST_CSV_HEADER,
    PROJECT_SCHEMA_VERSION,
    StepSymbol,
    TableBox,
    TextNote,
    WirePath,
    csv_text,
    normalize_dimension_orientation,
    normalize_wire_style,
    paper_preset_dimensions,
    paper_preset_for_dimensions,
    safe_name,
    try_float,
    wire_bom_rows,
    wire_netlist_rows,
)


class ProjectIOMixin:
    def _project_dict(self) -> dict:
        return {
            "schema_version": PROJECT_SCHEMA_VERSION,
            "paper": {
                "width_mm": self.paper_w_mm,
                "height_mm": self.paper_h_mm,
                "frame_margin_mm": self.frame_margin_mm,
                "banner_h_mm": self.banner_h_mm,
                "banner_w_mm": self.banner_w_mm,
                "preset": paper_preset_for_dimensions(self.paper_w_mm, self.paper_h_mm),
            },
            "meta": {
                "name": self.project_name_var.get().strip(),
                "revision": self.rev_var.get().strip(),
                "engineer": self.engineer_var.get().strip(),
                "drawing_number": self.drawing_number_var.get().strip(),
                "customer": self.customer_var.get().strip(),
                "checked_by": self.checked_by_var.get().strip(),
                "approved_by": self.approved_by_var.get().strip(),
                "date_drawn": self.date_drawn_var.get().strip(),
                "date_checked": self.date_checked_var.get().strip(),
                "date_approved": self.date_approved_var.get().strip(),
                "scale": self.scale_text_var.get().strip(),
                "sheet": self.sheet_var.get().strip(),
                "unit": self.unit_var.get().strip(),
                "tol_x": self.tol_x_var.get().strip(),
                "tol_xx": self.tol_xx_var.get().strip(),
                "tol_xxx": self.tol_xxx_var.get().strip(),
            },
            "defaults": {
                "wire_color": self.default_wire_color,
                "wire_color_b": self.default_wire_color_b,
                "wire_width_mm": self.default_wire_width_mm,
                "wire_style": self.default_wire_style,
                "wire_curve_offset_mm": self.default_wire_curve_offset_mm,
                "wire_twist_pitch_mm": self.default_wire_twist_pitch_mm,
                "wire_pair_gap_mm": self.default_wire_pair_gap_mm,
                "leader_color": self.default_leader_color,
                "leader_width_mm": self.default_leader_width_mm,
                "leader_arrow_size_mm": self.default_leader_arrow_size_mm,
                "leader_text_size_pt": self.default_leader_text_size_pt,
                "leader_text_box": self.default_leader_text_box,
                "dimension_color": self.default_dimension_color,
                "dimension_line_width_mm": self.default_dimension_line_width_mm,
                "dimension_arrow_size_mm": self.default_dimension_arrow_size_mm,
                "dimension_text_size_pt": self.default_dimension_text_size_pt,
                "dimension_offset_mm": self.default_dimension_offset_mm,
                "dimension_orientation": self.default_dimension_orientation,
                "dimension_tolerance": self.default_dimension_tolerance,
                "connector_line_color": self.default_connector_line_color,
                "connector_line_width_mm": self.default_connector_line_width_mm,
                "table_border_color": self.default_table_border_color,
                "table_border_width_mm": self.default_table_border_width_mm,
            },
            "view": {
                "zoom": self.zoom,
                "pan_x": self.pan_x,
                "pan_y": self.pan_y,
                "snap_grid_enabled": bool(self.snap_grid_enabled_var.get()),
                "snap_grid_mm": self._grid_step_mm(),
                "snap_endpoint_enabled": bool(self.snap_endpoint_enabled_var.get()),
            },
            "wire_bridges": {
                "enabled": bool(self.wire_bridge_enabled),
                "height_mm": self.wire_bridge_height_mm,
                "length_mm": self.wire_bridge_length_mm,
                "clearance_mm": self.wire_bridge_clearance_mm,
            },
            "symbols": [asdict(sym) for sym in self.symbols.values()],
            "connectors": [asdict(c) for c in self.connectors],
            "wires": [asdict(w) for w in self.wires],
            "leaders": [asdict(l) for l in self.leaders],
            "dimensions": [asdict(d) for d in self.dimensions],
            "text_notes": [asdict(n) for n in self.text_notes],
            "image_notes": [asdict(n) for n in self.image_notes],
            "tables": [asdict(t) for t in self.tables],
        }

    def _load_project_dict(self, data: dict):
        self._clear_connector_caches()
        self._clear_wire_caches()
        paper = data.get("paper", {})
        self.paper_w_mm = float(paper.get("width_mm", self.paper_w_mm))
        self.paper_h_mm = float(paper.get("height_mm", self.paper_h_mm))
        self.frame_margin_mm = float(paper.get("frame_margin_mm", self.frame_margin_mm))
        self.banner_h_mm = float(paper.get("banner_h_mm", self.banner_h_mm))
        self.banner_w_mm = float(paper.get("banner_w_mm", self.banner_w_mm))

        meta = data.get("meta", {})
        self.project_name_var.set(str(meta.get("name", "Nieuwe kabelboom tekening")))
        self.rev_var.set(str(meta.get("revision", "A")))
        self.engineer_var.set(str(meta.get("engineer", "")))
        self.drawing_number_var.set(str(meta.get("drawing_number", "")))
        self.customer_var.set(str(meta.get("customer", "")))
        self.checked_by_var.set(str(meta.get("checked_by", "")))
        self.approved_by_var.set(str(meta.get("approved_by", "")))
        self.date_drawn_var.set(str(meta.get("date_drawn", "")))
        self.date_checked_var.set(str(meta.get("date_checked", "")))
        self.date_approved_var.set(str(meta.get("date_approved", "")))
        self.scale_text_var.set(str(meta.get("scale", "NTS")))
        self.sheet_var.set(str(meta.get("sheet", "1 OF 1")))
        self.unit_var.set(str(meta.get("unit", "mm")))
        self.tol_x_var.set(str(meta.get("tol_x", "±0.25")))
        self.tol_xx_var.set(str(meta.get("tol_xx", "±0.1")))
        self.tol_xxx_var.set(str(meta.get("tol_xxx", "±0.05")))

        defaults = data.get("defaults", {})
        self.default_wire_color = str(defaults.get("wire_color", self.default_wire_color))
        self.default_wire_color_b = str(defaults.get("wire_color_b", self.default_wire_color_b))
        self.default_wire_width_mm = max(0.2, try_float(defaults.get("wire_width_mm", self.default_wire_width_mm), self.default_wire_width_mm))
        wire_style = str(defaults.get("wire_style", self.default_wire_style))
        self.default_wire_style = normalize_wire_style(wire_style)
        self.default_wire_curve_offset_mm = try_float(defaults.get("wire_curve_offset_mm", self.default_wire_curve_offset_mm), self.default_wire_curve_offset_mm)
        self.default_wire_twist_pitch_mm = max(
            1.0, try_float(defaults.get("wire_twist_pitch_mm", self.default_wire_twist_pitch_mm), self.default_wire_twist_pitch_mm)
        )
        self.default_wire_pair_gap_mm = max(
            0.2, try_float(defaults.get("wire_pair_gap_mm", self.default_wire_pair_gap_mm), self.default_wire_pair_gap_mm)
        )
        self.default_leader_color = str(defaults.get("leader_color", self.default_leader_color))
        self.default_leader_width_mm = max(0.2, try_float(defaults.get("leader_width_mm", self.default_leader_width_mm), self.default_leader_width_mm))
        self.default_leader_arrow_size_mm = max(
            0.5,
            try_float(defaults.get("leader_arrow_size_mm", self.default_leader_arrow_size_mm), self.default_leader_arrow_size_mm),
        )
        self.default_leader_text_size_pt = max(
            4.0,
            try_float(defaults.get("leader_text_size_pt", self.default_leader_text_size_pt), self.default_leader_text_size_pt),
        )
        self.default_leader_text_box = bool(defaults.get("leader_text_box", self.default_leader_text_box))
        self.default_dimension_color = str(defaults.get("dimension_color", self.default_dimension_color))
        self.default_dimension_line_width_mm = max(
            0.1, try_float(defaults.get("dimension_line_width_mm", self.default_dimension_line_width_mm), self.default_dimension_line_width_mm)
        )
        self.default_dimension_arrow_size_mm = max(
            0.5, try_float(defaults.get("dimension_arrow_size_mm", self.default_dimension_arrow_size_mm), self.default_dimension_arrow_size_mm)
        )
        self.default_dimension_text_size_pt = max(
            4.0, try_float(defaults.get("dimension_text_size_pt", self.default_dimension_text_size_pt), self.default_dimension_text_size_pt)
        )
        self.default_dimension_offset_mm = try_float(defaults.get("dimension_offset_mm", self.default_dimension_offset_mm), self.default_dimension_offset_mm)
        self.default_dimension_orientation = normalize_dimension_orientation(str(defaults.get("dimension_orientation", self.default_dimension_orientation)))
        self.default_dimension_tolerance = str(defaults.get("dimension_tolerance", self.default_dimension_tolerance))
        self.default_connector_line_color = str(defaults.get("connector_line_color", self.default_connector_line_color))
        self.default_connector_line_width_mm = max(
            0.1,
            try_float(defaults.get("connector_line_width_mm", self.default_connector_line_width_mm), self.default_connector_line_width_mm),
        )
        self.default_table_border_color = str(defaults.get("table_border_color", self.default_table_border_color))
        self.default_table_border_width_mm = max(
            0.1,
            try_float(defaults.get("table_border_width_mm", self.default_table_border_width_mm), self.default_table_border_width_mm),
        )

        view = data.get("view", {})
        self.zoom = clamp(try_float(view.get("zoom", self.zoom), self.zoom), 0.3, 8.0)
        self.pan_x = try_float(view.get("pan_x", self.pan_x), self.pan_x)
        self.pan_y = try_float(view.get("pan_y", self.pan_y), self.pan_y)
        self.snap_grid_enabled_var.set(bool(view.get("snap_grid_enabled", self.snap_grid_enabled_var.get())))
        self.snap_grid_mm_var.set(f'{max(0.1, try_float(view.get("snap_grid_mm", self._grid_step_mm()), self._grid_step_mm())):g}')
        self.snap_endpoint_enabled_var.set(bool(view.get("snap_endpoint_enabled", self.snap_endpoint_enabled_var.get())))

        wire_bridges = data.get("wire_bridges", {})
        if not isinstance(wire_bridges, dict):
            wire_bridges = {}
        self.wire_bridge_enabled = bool(wire_bridges.get("enabled", self.wire_bridge_enabled))
        self.wire_bridge_height_mm = max(
            0.1,
            try_float(wire_bridges.get("height_mm", self.wire_bridge_height_mm), self.wire_bridge_height_mm),
        )
        self.wire_bridge_length_mm = max(
            0.5,
            try_float(wire_bridges.get("length_mm", self.wire_bridge_length_mm), self.wire_bridge_length_mm),
        )
        self.wire_bridge_clearance_mm = max(
            self.wire_bridge_length_mm,
            try_float(wire_bridges.get("clearance_mm", self.wire_bridge_clearance_mm), self.wire_bridge_clearance_mm),
        )
        self._sync_wire_bridge_vars()

        self.symbols = {}
        for raw in data.get("symbols", []):
            try:
                sym = StepSymbol(
                    name=str(raw["name"]),
                    source_path=str(raw.get("source_path", "")),
                    projection=str(raw.get("projection", "Top (XY)")),
                    polylines=[[(float(x), float(y)) for x, y in line] for line in raw.get("polylines", [])],
                    width_mm=float(raw.get("width_mm", 10.0)),
                    height_mm=float(raw.get("height_mm", 10.0)),
                )
                self.symbols[sym.name] = sym
            except Exception:
                continue

        self.connectors = []
        for raw in data.get("connectors", []):
            try:
                self.connectors.append(
                    ConnectorInstance(
                        connector_id=str(raw["connector_id"]),
                        symbol_name=str(raw["symbol_name"]),
                        x_mm=float(raw["x_mm"]),
                        y_mm=float(raw["y_mm"]),
                        scale=float(raw.get("scale", 1.0)),
                        rotation_deg=float(raw.get("rotation_deg", 0.0)),
                        mirror_x=bool(raw.get("mirror_x", False)),
                        mirror_y=bool(raw.get("mirror_y", False)),
                        line_color=str(raw.get("line_color", self.default_connector_line_color)),
                        line_width_mm=float(raw.get("line_width_mm", self.default_connector_line_width_mm)),
                        note=str(raw.get("note", "")),
                        part_number=str(raw.get("part_number", "")),
                        pin_count=max(1, int(raw.get("pin_count", 1))),
                        pin_labels=[str(label).strip() for label in raw.get("pin_labels", []) if str(label).strip()],
                    )
                )
            except Exception:
                continue

        self.wires = []
        wire_ids_seen: set[str] = set()
        for raw in data.get("wires", []):
            try:
                points = [(float(x), float(y)) for x, y in raw.get("points_mm", [])]
                if len(points) < 2:
                    continue
                base_id = str(raw.get("wire_id", "")).strip() or self._next_id("W", list(wire_ids_seen))
                color = str(raw.get("color", "#1f4e79"))
                color_b = str(raw.get("color_b", "#d7263d"))
                width_mm = float(raw.get("width_mm", 1.2))
                style = normalize_wire_style(str(raw.get("style", "straight")).strip() or "straight")
                curve_offset = float(raw.get("curve_offset_mm", 8.0))
                start_handle_raw = raw.get("start_handle_offset_mm", (0.0, 0.0))
                end_handle_raw = raw.get("end_handle_offset_mm", (0.0, 0.0))
                try:
                    start_handle_offset = (float(start_handle_raw[0]), float(start_handle_raw[1]))
                except Exception:
                    start_handle_offset = (0.0, 0.0)
                try:
                    end_handle_offset = (float(end_handle_raw[0]), float(end_handle_raw[1]))
                except Exception:
                    end_handle_offset = (0.0, 0.0)
                twist_pitch = float(raw.get("twist_pitch_mm", 10.0))
                pair_gap = float(raw.get("pair_gap_mm", 2.8))
                label = str(raw.get("label", ""))
                signal_name = str(raw.get("signal_name", ""))
                from_connector = str(raw.get("from_connector", "")).strip().upper()
                from_pin = str(raw.get("from_pin", "")).strip()
                to_connector = str(raw.get("to_connector", "")).strip().upper()
                to_pin = str(raw.get("to_pin", "")).strip()
                cross_section = max(0.0, try_float(raw.get("cross_section_mm2", 0.35), 0.35))
                length_mm = max(0.0, try_float(raw.get("length_mm", 0.0), 0.0))
                shielded = bool(raw.get("shielded", False))
                net_name = str(raw.get("net_name", ""))

                def unique_wire_id(candidate: str) -> str:
                    out = candidate
                    suffix = 1
                    while out in wire_ids_seen:
                        suffix += 1
                        out = f"{candidate}_{suffix}"
                    wire_ids_seen.add(out)
                    return out

                if len(points) == 2:
                    wid = unique_wire_id(base_id)
                    self.wires.append(
                        WirePath(
                            wire_id=wid,
                            points_mm=points,
                            color=color,
                            color_b=color_b,
                            width_mm=width_mm,
                            style=style,
                            curve_offset_mm=curve_offset,
                            start_handle_offset_mm=start_handle_offset,
                            end_handle_offset_mm=end_handle_offset,
                            twist_pitch_mm=twist_pitch,
                            pair_gap_mm=pair_gap,
                            label=label,
                            signal_name=signal_name,
                            from_connector=from_connector,
                            from_pin=from_pin,
                            to_connector=to_connector,
                            to_pin=to_pin,
                            cross_section_mm2=cross_section,
                            length_mm=length_mm,
                            shielded=shielded,
                            net_name=net_name,
                        )
                    )
                else:
                    for i in range(len(points) - 1):
                        seg_id = unique_wire_id(f"{base_id}_S{i+1:02d}")
                        seg_label = label if i == 0 else ""
                        self.wires.append(
                            WirePath(
                                wire_id=seg_id,
                                points_mm=[points[i], points[i + 1]],
                                color=color,
                                color_b=color_b,
                                width_mm=width_mm,
                                style=style,
                                curve_offset_mm=curve_offset,
                                start_handle_offset_mm=start_handle_offset,
                                end_handle_offset_mm=end_handle_offset,
                                twist_pitch_mm=twist_pitch,
                                pair_gap_mm=pair_gap,
                                label=seg_label,
                                signal_name=signal_name,
                                from_connector=from_connector,
                                from_pin=from_pin,
                                to_connector=to_connector,
                                to_pin=to_pin,
                                cross_section_mm2=cross_section,
                                length_mm=length_mm,
                                shielded=shielded,
                                net_name=net_name,
                            )
                        )
            except Exception:
                continue

        self.leaders = []
        for raw in data.get("leaders", []):
            try:
                self.leaders.append(
                    Leader(
                        leader_id=str(raw["leader_id"]),
                        start_mm=(float(raw["start_mm"][0]), float(raw["start_mm"][1])),
                        end_mm=(float(raw["end_mm"][0]), float(raw["end_mm"][1])),
                        text=str(raw.get("text", "")),
                        color=str(raw.get("color", self.default_leader_color)),
                        width_mm=float(raw.get("width_mm", self.default_leader_width_mm)),
                        arrow_size_mm=max(
                            0.5,
                            try_float(raw.get("arrow_size_mm", self.default_leader_arrow_size_mm), self.default_leader_arrow_size_mm),
                        ),
                        text_size_pt=max(
                            4.0,
                            try_float(raw.get("text_size_pt", self.default_leader_text_size_pt), self.default_leader_text_size_pt),
                        ),
                        text_box=bool(raw.get("text_box", self.default_leader_text_box)),
                    )
                )
            except Exception:
                continue

        self.dimensions = []
        for raw in data.get("dimensions", []):
            try:
                self.dimensions.append(
                    DimensionLine(
                        dim_id=str(raw["dim_id"]),
                        p1_mm=(float(raw["p1_mm"][0]), float(raw["p1_mm"][1])),
                        p2_mm=(float(raw["p2_mm"][0]), float(raw["p2_mm"][1])),
                        orientation=normalize_dimension_orientation(str(raw.get("orientation", "horizontal"))),
                        offset_mm=try_float(raw.get("offset_mm", self.default_dimension_offset_mm), self.default_dimension_offset_mm),
                        color=str(raw.get("color", self.default_dimension_color)),
                        line_width_mm=max(0.1, try_float(raw.get("line_width_mm", self.default_dimension_line_width_mm), self.default_dimension_line_width_mm)),
                        arrow_size_mm=max(0.5, try_float(raw.get("arrow_size_mm", self.default_dimension_arrow_size_mm), self.default_dimension_arrow_size_mm)),
                        text_size_pt=max(4.0, try_float(raw.get("text_size_pt", self.default_dimension_text_size_pt), self.default_dimension_text_size_pt)),
                        tolerance=str(raw.get("tolerance", "")),
                        override_text=str(raw.get("override_text", "")),
                        decimals=max(0, int(try_float(raw.get("decimals", 0), 0))),
                    )
                )
            except Exception:
                continue

        self.text_notes = []
        for raw in data.get("text_notes", []):
            try:
                self.text_notes.append(
                    TextNote(
                        note_id=str(raw["note_id"]),
                        x_mm=float(raw["x_mm"]),
                        y_mm=float(raw["y_mm"]),
                        text=str(raw.get("text", "")),
                        color=str(raw.get("color", "#1f2937")),
                        font_size_pt=float(raw.get("font_size_pt", 10.0)),
                    )
                )
            except Exception:
                continue

        self.image_notes = []
        for raw in data.get("image_notes", []):
            try:
                self.image_notes.append(
                    ImageNote(
                        image_id=str(raw["image_id"]),
                        x_mm=float(raw["x_mm"]),
                        y_mm=float(raw["y_mm"]),
                        width_mm=max(1.0, float(raw.get("width_mm", 40.0))),
                        height_mm=max(1.0, float(raw.get("height_mm", 30.0))),
                        scale=max(0.05, float(raw.get("scale", 1.0))),
                        source_path=str(raw.get("source_path", "")),
                        image_data_b64=str(raw.get("image_data_b64", "")),
                        mime_type=str(raw.get("mime_type", "image/png")),
                    )
                )
            except Exception:
                continue

        self.tables = []
        for raw in data.get("tables", []):
            try:
                t = TableBox(
                    table_id=str(raw["table_id"]),
                    x_mm=float(raw["x_mm"]),
                    y_mm=float(raw["y_mm"]),
                    cols=max(1, int(raw.get("cols", 3))),
                    rows=max(1, int(raw.get("rows", 4))),
                    cell_w_mm=float(raw.get("cell_w_mm", 20.0)),
                    cell_h_mm=float(raw.get("cell_h_mm", 8.0)),
                    border_color=str(raw.get("border_color", self.default_table_border_color)),
                    border_width_mm=float(raw.get("border_width_mm", self.default_table_border_width_mm)),
                    col_widths_mm=[float(x) for x in raw.get("col_widths_mm", [])],
                    row_heights_mm=[float(x) for x in raw.get("row_heights_mm", [])],
                    text_h_align=str(raw.get("text_h_align", "center")),
                    text_v_align=str(raw.get("text_v_align", "middle")),
                    is_border=bool(raw.get("is_border", False)),
                    cells=[[str(c) for c in row] for row in raw.get("cells", [])],
                )
                if t.text_h_align not in {"left", "center", "right"}:
                    t.text_h_align = "center"
                if t.text_v_align not in {"top", "middle", "bottom"}:
                    t.text_v_align = "middle"
                while len(t.cells) < t.rows:
                    t.cells.append(["" for _ in range(t.cols)])
                t.cells = [row[: t.cols] + ([""] * max(0, t.cols - len(row))) for row in t.cells[: t.rows]]
                self.tables.append(t)
            except Exception:
                continue

        self._refresh_symbol_list()
        self._sync_paper_preset_var()
        self._clear_selection()
        self.load_selection_properties_to_panel()
        self.cancel_temporary_action()
        self.redraw()

    def new_project(self):
        if not self._confirm_discard_changes():
            return
        self.project_path = None
        self._clear_connector_caches()
        self._clear_wire_caches()
        self.symbols = {}
        self.connectors = []
        self.wires = []
        self.leaders = []
        self.dimensions = []
        self.text_notes = []
        self.image_notes = []
        self.tables = []
        default_paper = paper_preset_dimensions(DEFAULT_PAPER_PRESET) or (420.0, 297.0)
        self.paper_w_mm = default_paper[0]
        self.paper_h_mm = default_paper[1]
        self._sync_paper_preset_var()
        self.project_name_var.set("Nieuwe kabelboom tekening")
        self.rev_var.set("A")
        self.engineer_var.set("")
        self.drawing_number_var.set("")
        self.customer_var.set("")
        self.checked_by_var.set("")
        self.approved_by_var.set("")
        self.date_drawn_var.set("")
        self.date_checked_var.set("")
        self.date_approved_var.set("")
        self.scale_text_var.set("NTS")
        self.sheet_var.set("1 OF 1")
        self.unit_var.set("mm")
        self.tol_x_var.set("±0.25")
        self.tol_xx_var.set("±0.1")
        self.tol_xxx_var.set("±0.05")
        self.wire_bridge_enabled = DEFAULT_WIRE_BRIDGE_ENABLED
        self.wire_bridge_height_mm = DEFAULT_WIRE_BRIDGE_HEIGHT_MM
        self.wire_bridge_length_mm = DEFAULT_WIRE_BRIDGE_LENGTH_MM
        self.wire_bridge_clearance_mm = DEFAULT_WIRE_BRIDGE_CLEARANCE_MM
        self._sync_wire_bridge_vars()
        self.set_mode("select")
        self._clear_selection()
        self._refresh_symbol_list()
        self.load_selection_properties_to_panel()
        self._reset_history()
        self.redraw()
        self._mark_saved()
        self.status("Nieuw project")

    def open_project(self):
        if not self._confirm_discard_changes():
            return
        path = self._ask_open_filename(
            title="Open tekenproject",
            filetypes=[("Kabelboom Drawing JSON", "*.json"), ("Alle bestanden", "*.*")],
            settings_key="last_project_dir",
        )
        if not path:
            return
        try:
            payload = json.loads(Path(path).read_text(encoding="utf-8"))
            self._load_project_dict(payload)
            self.project_path = Path(path)
            self._remember_dialog_path("last_project_dir", path)
            self._reset_history()
            self._mark_saved()
            self.status(f"Geopend: {self.project_path.name}")
        except Exception as exc:
            self._show_error(f"Openen mislukt:\n{exc}")

    def save_project(self):
        if self.project_path is None:
            return self.save_project_as()
        return self._save_project_to(self.project_path)

    def save_project_as(self):
        name = safe_name(self.project_name_var.get(), "kabelboom_drawing")
        path = self._ask_save_filename(
            title="Project opslaan als",
            defaultextension=".json",
            initialfile=f"{name}.json",
            filetypes=[("Kabelboom Drawing JSON", "*.json"), ("Alle bestanden", "*.*")],
            settings_key="last_project_dir",
        )
        if not path:
            return False
        self.project_path = Path(path)
        return self._save_project_to(self.project_path)

    def _save_project_to(self, path: Path):
        try:
            write_text_atomic(path, json.dumps(self._project_dict(), indent=2, ensure_ascii=False))
            self._mark_saved()
            self.status(f"Opgeslagen: {path.name}")
            return True
        except Exception as exc:
            self._show_error(f"Opslaan mislukt:\n{exc}")
            return False

    def export_svg(self):
        name = safe_name(self.project_name_var.get(), "kabelboom")
        path = self._ask_save_filename(
            title="Exporteer SVG",
            defaultextension=".svg",
            initialfile=f"{name}_tekening.svg",
            filetypes=[("SVG", "*.svg"), ("Alle bestanden", "*.*")],
            settings_key="last_export_dir",
        )
        if not path:
            return
        try:
            self._write_svg(Path(path))
            self.status(f"SVG geexporteerd: {Path(path).name}")
            self._show_info(f"SVG gemaakt:\n{path}")
        except Exception as exc:
            self._show_error(f"SVG export mislukt:\n{exc}")

    def export_netlist_csv(self):
        name = safe_name(self.project_name_var.get(), "kabelboom")
        path = self._ask_save_filename(
            title="Exporteer netlist CSV",
            defaultextension=".csv",
            initialfile=f"{name}_netlist.csv",
            filetypes=[("CSV", "*.csv"), ("Alle bestanden", "*.*")],
            settings_key="last_export_dir",
        )
        if not path:
            return
        try:
            self._write_netlist_csv(Path(path))
            self.status(f"Netlist CSV geexporteerd: {Path(path).name}")
            self._show_info(f"Netlist CSV gemaakt:\n{path}")
        except Exception as exc:
            self._show_error(f"Netlist CSV export mislukt:\n{exc}")

    def export_bom_csv(self):
        name = safe_name(self.project_name_var.get(), "kabelboom")
        path = self._ask_save_filename(
            title="Exporteer BOM CSV",
            defaultextension=".csv",
            initialfile=f"{name}_bom.csv",
            filetypes=[("CSV", "*.csv"), ("Alle bestanden", "*.*")],
            settings_key="last_export_dir",
        )
        if not path:
            return
        try:
            self._write_bom_csv(Path(path))
            self.status(f"BOM CSV geexporteerd: {Path(path).name}")
            self._show_info(f"BOM CSV gemaakt:\n{path}")
        except Exception as exc:
            self._show_error(f"BOM CSV export mislukt:\n{exc}")

    def _write_netlist_csv(self, path: Path):
        write_text_atomic(path, csv_text(NETLIST_CSV_HEADER, wire_netlist_rows(self.wires)))

    def _write_bom_csv(self, path: Path):
        write_text_atomic(path, csv_text(BOM_CSV_HEADER, wire_bom_rows(self.wires)))
