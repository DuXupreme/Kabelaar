"""Canvas- en SVG-rendering voor de tekenstudio.

RenderingMixin bevat alle teken- en SVG-exportmethodes. De hoofdklasse
HarnessDrawingStudio erft ervan; alle verwijzingen naar self lossen op tegen
die instantie. Bevat bewust geen state of __init__.
"""

from __future__ import annotations

import json
import time
import tkinter as tk
from pathlib import Path
from typing import List, Tuple
from xml.sax.saxutils import escape

from geometry import clamp
from model import DimensionLine, WirePath, normalize_wire_style

try:
    from PIL import ImageTk

    from aa_render import PageImageCache, render_viewport_image

    AA_RENDER_AVAILABLE = True
except (ImportError, OSError):
    ImageTk = None
    PageImageCache = None
    render_viewport_image = None
    AA_RENDER_AVAILABLE = False


class RenderingMixin:
    def redraw(self):
        self._redraw_scheduled = False
        _perf_t0 = time.perf_counter()
        cv = self.canvas
        cv.delete("all")
        self._canvas_image_refs = []

        # Batch 7A: de volledige statische scène is één anti-aliased bitmap. Alleen
        # selectiehandles, drag-objecten en tijdelijke geometrie blijven Tk-overlays.
        if self._draw_aa_scene():
            self._draw_interaction_overlays()
            self._draw_temporary_geometry()
            self._draw_empty_state_overlay()
            self._record_redraw_time((time.perf_counter() - _perf_t0) * 1000.0)
            return

        # Paper background
        px1, py1 = self.world_to_canvas(0, 0)
        px2, py2 = self.world_to_canvas(self.paper_w_mm, self.paper_h_mm)
        cv.create_rectangle(px1, py1, px2, py2, fill="white", outline="#7c8aa0", width=2)

        # Technical frame
        f = self.frame_margin_mm
        fx1, fy1 = self.world_to_canvas(f, f)
        fx2, fy2 = self.world_to_canvas(self.paper_w_mm - f, self.paper_h_mm - f)
        cv.create_rectangle(fx1, fy1, fx2, fy2, outline="#25364a", width=2)

        def _canvas_font_px(pt: float) -> int:
            return max(5, int(round(pt * 0.3528 * self.zoom)))

        # Zone markers (numbers across, letters down)
        zone = self._zone_marker_drawing()
        for zx1, zy1, zx2, zy2 in zone["lines"]:
            za = self.world_to_canvas(zx1, zy1)
            zb = self.world_to_canvas(zx2, zy2)
            cv.create_line(za[0], za[1], zb[0], zb[1], fill=zone["color"], width=1)
        for t in zone["texts"]:
            tp = self.world_to_canvas(t["x"], t["y"])
            cv.create_text(tp[0], tp[1], text=t["text"], fill=zone["color"], font=("Segoe UI", -_canvas_font_px(t["pt"])), anchor="center")

        # Banner / title block
        title_block = self._title_block_drawing()
        tb_x1, tb_y1, tb_x2, tb_y2 = title_block["rect"]
        r1 = self.world_to_canvas(tb_x1, tb_y1)
        r2 = self.world_to_canvas(tb_x2, tb_y2)
        cv.create_rectangle(r1[0], r1[1], r2[0], r2[1], fill="white", outline="")
        for lx1, ly1, lx2, ly2 in title_block["lines"]:
            la = self.world_to_canvas(lx1, ly1)
            lb = self.world_to_canvas(lx2, ly2)
            cv.create_line(la[0], la[1], lb[0], lb[1], fill=title_block["line_color"], width=1.5)
        for t in title_block["texts"]:
            tp = self.world_to_canvas(t["x"], t["y"])
            anchor = "center" if t["anchor"] == "mm" else "nw"
            px = _canvas_font_px(t["pt"])
            font = ("Segoe UI", -px, "bold") if t["bold"] else ("Segoe UI", -px)
            cv.create_text(tp[0], tp[1], text=t["text"], fill=title_block["text_color"], font=font, anchor=anchor)

        # Grid
        grid_mm = self._grid_step_mm() if self.snap_grid_enabled_var.get() else 10.0
        grid_mm = max(1.0, grid_mm)
        if self.zoom >= 1.2:
            x = f
            while x <= self.paper_w_mm - f:
                x1, y1 = self.world_to_canvas(x, f)
                x2, y2 = self.world_to_canvas(x, self.paper_h_mm - f)
                cv.create_line(x1, y1, x2, y2, fill="#edf2f7")
                x += grid_mm
            y = f
            while y <= self.paper_h_mm - f:
                x1, y1 = self.world_to_canvas(f, y)
                x2, y2 = self.world_to_canvas(self.paper_w_mm - f, y)
                cv.create_line(x1, y1, x2, y2, fill="#edf2f7")
                y += grid_mm

        self._draw_image_notes()
        self._draw_connectors()
        self._draw_wires()
        self._draw_leaders()
        self._draw_dimensions()
        self._draw_tables()
        self._draw_text_notes()
        self._draw_temporary_geometry()
        self._draw_empty_state_overlay()
        self._record_redraw_time((time.perf_counter() - _perf_t0) * 1000.0)

    def _aa_scene_content_signature(self) -> Tuple:
        """Modelsignature zonder pan/zoom of selectie; die zijn goedkope overlays."""

        if bool(getattr(self, "_aa_scene_dirty", True)) or getattr(self, "_aa_model_signature", None) is None:
            data = self._project_dict()
            data.pop("view", None)
            payload = json.dumps(data, ensure_ascii=False, sort_keys=True, separators=(",", ":"))
            self._aa_model_signature = (hash(payload), len(payload))
            self._aa_scene_dirty = False
        grid_signature = (
            bool(self.snap_grid_enabled_var.get()),
            round(float(self._grid_step_mm()), 4),
            bool(self.zoom >= 1.2),
        )
        drag_filter = getattr(self, "_drag_filter", None)
        if drag_filter:
            drag_signature = (drag_filter[0], tuple(sorted(drag_filter[1])))
        else:
            drag_signature = None
        return (self._aa_model_signature, grid_signature, drag_signature)

    def _draw_aa_scene(self) -> bool:
        if not AA_RENDER_AVAILABLE or ImageTk is None or render_viewport_image is None:
            return False
        width = max(1, int(self.canvas.winfo_width()))
        height = max(1, int(self.canvas.winfo_height()))
        if width <= 1 or height <= 1:
            return False
        try:
            cache = getattr(self, "_aa_scene_cache", None)
            if cache is None:
                cache = PageImageCache()
                self._aa_scene_cache = cache
            show_grid = bool(self.zoom >= 1.2)
            signature = self._aa_scene_content_signature()
            viewport = render_viewport_image(
                lambda dpi: self._render_page_image(dpi=dpi, show_grid=show_grid),
                cache,
                content_signature=signature,
                paper_width_mm=self.paper_w_mm,
                paper_height_mm=self.paper_h_mm,
                canvas_width_px=width,
                canvas_height_px=height,
                zoom_px_per_mm=self.zoom,
                pan_x_px=self.pan_x,
                pan_y_px=self.pan_y,
                background=str(self.canvas.cget("background")),
                sharp=not bool(getattr(self, "_aa_zoom_preview", False)),
                supersample=2,
            )
            photo = getattr(self, "_aa_viewport_photo", None)
            if photo is None or getattr(self, "_aa_viewport_pil", None) is not viewport:
                photo = ImageTk.PhotoImage(viewport)
                self._aa_viewport_photo = photo
                self._aa_viewport_pil = viewport
            self._canvas_image_refs.append(photo)
            self.canvas.create_image(0, 0, anchor="nw", image=photo, tags=("aa_scene",))
            self._aa_render_error = ""
            return True
        except Exception as exc:
            # Pillow is optioneel; bij een fout blijft de oude Tk-renderer bruikbaar.
            self._aa_render_error = str(exc)
            return False

    def _draw_interaction_overlays(self):
        """Teken uitsluitend selectie- en reshapehandles boven de AA-scène."""

        primary_wire_id = self.selected[1] if self.selected and self.selected[0] == "wire" else None
        selected_wire_ids = self._selected_wire_id_set_for_drawing()
        for wire in self.wires:
            if wire.wire_id not in selected_wire_ids or self._drag_render_skip("wire", wire.wire_id):
                continue
            endpoints = self._wire_endpoints(wire)
            if endpoints is None:
                continue
            for x, y in endpoints:
                px, py = self.world_to_canvas(x, y)
                if primary_wire_id == wire.wire_id:
                    self.canvas.create_oval(px - 6, py - 6, px + 6, py + 6, fill="white", outline="#d61f1f", width=2)
                    self.canvas.create_oval(px - 2, py - 2, px + 2, py + 2, fill="#d61f1f", outline="")
                else:
                    self.canvas.create_oval(px - 3, py - 3, px + 3, py + 3, fill="#d61f1f", outline="")
            if primary_wire_id == wire.wire_id and self._wire_has_tangent_handles(wire):
                handles = self._wire_tangent_handle_points(wire)
                start = self.world_to_canvas(*endpoints[0])
                end = self.world_to_canvas(*endpoints[1])
                start_handle = self.world_to_canvas(*handles["start_tangent"])
                end_handle = self.world_to_canvas(*handles["end_tangent"])
                self.canvas.create_line(*start, *start_handle, fill="#f08c00", dash=(4, 3), width=1)
                self.canvas.create_line(*end, *end_handle, fill="#f08c00", dash=(4, 3), width=1)
                for cx, cy in (start_handle, end_handle):
                    self.canvas.create_oval(cx - 5, cy - 5, cx + 5, cy + 5, fill="white", outline="#f08c00", width=2)
                    self.canvas.create_oval(cx - 2, cy - 2, cx + 2, cy + 2, fill="#f08c00", outline="")
            if primary_wire_id == wire.wire_id and self._wire_has_curve_handle(wire):
                mid_x = (endpoints[0][0] + endpoints[1][0]) / 2.0
                mid_y = (endpoints[0][1] + endpoints[1][1]) / 2.0
                control = self._curve_control_point(wire)
                midpoint_canvas = self.world_to_canvas(mid_x, mid_y)
                control_canvas = self.world_to_canvas(*control)
                self.canvas.create_line(*midpoint_canvas, *control_canvas, fill="#d61f1f", dash=(4, 3), width=1)
                cx, cy = control_canvas
                self.canvas.create_oval(cx - 6, cy - 6, cx + 6, cy + 6, fill="white", outline="#d61f1f", width=2)
                self.canvas.create_oval(cx - 2, cy - 2, cx + 2, cy + 2, fill="#d61f1f", outline="")

        for connector in self.connectors:
            if not self._is_item_selected("connector", connector.connector_id) or self._drag_render_skip("connector", connector.connector_id):
                continue
            bx1, by1, bx2, by2 = self._connector_world_bbox(connector)
            x1, y1 = self.world_to_canvas(bx1, by1)
            x2, y2 = self.world_to_canvas(bx2, by2)
            self.canvas.create_rectangle(x1, y1, x2, y2, outline="#d61f1f", dash=(4, 4), width=2)
            lbx1, lby1, lbx2, lby2 = self._connector_label_canvas_bbox(connector)
            self.canvas.create_rectangle(lbx1, lby1, lbx2, lby2, outline="#f08c00", dash=(2, 2), width=1)
            for pin_label, wx, wy in self._connector_pin_world_points(connector):
                px, py = self.world_to_canvas(wx, wy)
                self.canvas.create_oval(px - 3, py - 3, px + 3, py + 3, fill="#1d4ed8", outline="white", width=1)
                self.canvas.create_text(px + 5, py - 5, text=pin_label, anchor="sw", fill="#1d4ed8", font=("Segoe UI", 7))

        for note in self.image_notes:
            if self._is_item_selected("image", note.image_id) and not self._drag_render_skip("image", note.image_id):
                bbox = self._image_note_bbox(note)
                x1, y1 = self.world_to_canvas(bbox[0], bbox[1])
                x2, y2 = self.world_to_canvas(bbox[2], bbox[3])
                self.canvas.create_rectangle(x1, y1, x2, y2, outline="#d61f1f", dash=(4, 4), width=2)

        for note in self.text_notes:
            if self._is_item_selected("text", note.note_id) and not self._drag_render_skip("text", note.note_id):
                bbox = self._text_note_bbox(note)
                x1, y1 = self.world_to_canvas(bbox[0], bbox[1])
                x2, y2 = self.world_to_canvas(bbox[2], bbox[3])
                self.canvas.create_rectangle(x1, y1, x2, y2, outline="#d61f1f", dash=(4, 4), width=2)

        for leader in self.leaders:
            if not self._is_item_selected("leader", leader.leader_id) or self._drag_render_skip("leader", leader.leader_id):
                continue
            if not leader.text_box:
                bbox = self._leader_text_bbox(leader, include_empty=True)
                if bbox:
                    x1, y1 = self.world_to_canvas(bbox[0], bbox[1])
                    x2, y2 = self.world_to_canvas(bbox[2], bbox[3])
                    self.canvas.create_rectangle(x1, y1, x2, y2, fill="", outline="#d61f1f", dash=(3, 3), width=1)
            for x, y in (leader.start_mm, leader.end_mm):
                px, py = self.world_to_canvas(x, y)
                self.canvas.create_oval(px - 6, py - 6, px + 6, py + 6, fill="white", outline="#d61f1f", width=2)
                self.canvas.create_oval(px - 2, py - 2, px + 2, py + 2, fill="#d61f1f", outline="")

        for dimension in self.dimensions:
            if not self._is_item_selected("dimension", dimension.dim_id) or self._drag_render_skip("dimension", dimension.dim_id):
                continue
            for point in (dimension.p1_mm, dimension.p2_mm):
                px, py = self.world_to_canvas(*point)
                self.canvas.create_oval(px - 6, py - 6, px + 6, py + 6, fill="white", outline="#d61f1f", width=2)
                self.canvas.create_oval(px - 2, py - 2, px + 2, py + 2, fill="#d61f1f", outline="")

        for table in self.tables:
            if not self._is_item_selected("table", table.table_id) or self._drag_render_skip("table", table.table_id):
                continue
            width = sum(self._table_col_widths(table))
            height = sum(self._table_row_heights(table))
            x1, y1 = self.world_to_canvas(table.x_mm, table.y_mm)
            x2, y2 = self.world_to_canvas(table.x_mm + width, table.y_mm + height)
            self.canvas.create_rectangle(x1, y1, x2, y2, outline="#d61f1f", dash=(4, 4), width=2)

    def _record_redraw_time(self, elapsed_ms: float):
        """Houd de laatste redraw-tijden bij en toon ze als de prestatiemeter aanstaat."""
        self._last_redraw_ms = elapsed_ms
        samples = self._redraw_ms_samples
        samples.append(elapsed_ms)
        if len(samples) > 30:
            del samples[: len(samples) - 30]
        if not getattr(self, "_show_perf_meter", False):
            return
        avg = sum(samples) / len(samples)
        peak = max(samples)
        items = len(self.canvas.find_all())
        self.perf_var.set(
            f"⏱ {elapsed_ms:.1f} ms  ·  gem {avg:.1f}  ·  piek {peak:.1f}  ·  {items} items"
        )

    def _drag_render_skip(self, kind: str, ident: str) -> bool:
        """Render-filter voor incrementeel slepen. Bij ('skip', ids) wordt het versleepte
        object overgeslagen (de bevroren achtergrondlaag); bij ('only', ids) juist alléén
        dat object getekend (de losse 'dragmove'-laag die per beweging ververst)."""
        flt = getattr(self, "_drag_filter", None)
        if flt is None:
            return False
        mode, ids = flt
        in_set = (kind, ident) in ids
        return (not in_set) if mode == "only" else in_set

    def _project_is_empty(self) -> bool:
        return not any((
            self.connectors,
            self.wires,
            self.leaders,
            self.dimensions,
            self.tables,
            self.text_notes,
            self.image_notes,
        ))

    def _draw_empty_state_overlay(self):
        if not self._project_is_empty():
            return
        cv = self.canvas
        cw = cv.winfo_width()
        ch = cv.winfo_height()
        if cw <= 1 or ch <= 1:
            return
        cx = cw / 2.0
        cy = ch / 2.0
        lines = [
            ("Leeg tekenblad", 16, "bold", "#334155"),
            ("Importeer een STEP-connector (knop 'STEP import' links)", 11, "normal", "#64748b"),
            ("of kies een tekenmodus in de werkbalk bovenaan.", 11, "normal", "#64748b"),
            ("F1 toont alle sneltoetsen · Bestand ▸ Nieuw uit sjabloon…", 10, "normal", "#94a3b8"),
        ]
        offset = -36
        for text, pt, weight, color in lines:
            font = ("Segoe UI", pt, "bold") if weight == "bold" else ("Segoe UI", pt)
            cv.create_text(cx, cy + offset, text=text, fill=color, font=font, anchor="center")
            offset += pt + 12

    def _redraw_temporary_geometry_only(self):
        if not self.canvas.winfo_exists():
            return
        self.canvas.delete("temp_overlay")
        self._draw_temporary_geometry()

    def _draw_connectors(self):
        if not self.connectors:
            return
        view_x1, view_y1 = self.canvas_to_world(0, 0)
        view_x2, view_y2 = self.canvas_to_world(self.canvas.winfo_width(), self.canvas.winfo_height())
        margin_mm = max(8.0, 24.0 / max(0.3, self.zoom))
        visible_bounds = (
            min(view_x1, view_x2) - margin_mm,
            min(view_y1, view_y2) - margin_mm,
            max(view_x1, view_x2) + margin_mm,
            max(view_y1, view_y2) + margin_mm,
        )
        for c in self.connectors:
            if self._drag_render_skip("connector", c.connector_id):
                continue
            sym = self.symbols.get(c.symbol_name)
            if not sym:
                continue
            bx1, by1, bx2, by2 = self._connector_world_bbox(c)
            if bx2 < visible_bounds[0] or bx1 > visible_bounds[2] or by2 < visible_bounds[1] or by1 > visible_bounds[3]:
                continue
            stroke_width_px = max(1.0, c.line_width_mm * self.zoom * 0.22)
            rendered_as_image = False
            if self._symbol_requires_raster_preview(sym):
                canvas_image = self._connector_canvas_image(c, stroke_width_px)
                if canvas_image:
                    photo, left_px, top_px = canvas_image
                    self._canvas_image_refs.append(photo)
                    self.canvas.create_image(left_px, top_px, anchor="nw", image=photo)
                    rendered_as_image = True

            if not rendered_as_image:
                for pts in self._connector_canvas_polylines(c):
                    self.canvas.create_line(*pts, fill=c.line_color, width=stroke_width_px, smooth=False)

            lx, ly = self.world_to_canvas(c.x_mm + c.label_dx_mm, c.y_mm + c.label_dy_mm)
            self.canvas.create_text(lx, ly, text=c.connector_id, fill="#0d2238", font=("Segoe UI", 9, "bold"))

            if self._is_item_selected("connector", c.connector_id):
                x1, y1 = self.world_to_canvas(bx1, by1)
                x2, y2 = self.world_to_canvas(bx2, by2)
                self.canvas.create_rectangle(x1, y1, x2, y2, outline="#d61f1f", dash=(4, 4), width=2)
                # Sleephandle rond het label zodat de naam los verplaatsbaar is.
                lbx1, lby1, lbx2, lby2 = self._connector_label_canvas_bbox(c)
                self.canvas.create_rectangle(lbx1, lby1, lbx2, lby2, outline="#f08c00", dash=(2, 2), width=1)
                # Pin-markers met nummer/label.
                for pin_label, wx, wy in self._connector_pin_world_points(c):
                    ppx, ppy = self.world_to_canvas(wx, wy)
                    self.canvas.create_oval(ppx - 3, ppy - 3, ppx + 3, ppy + 3, fill="#1d4ed8", outline="white", width=1)
                    self.canvas.create_text(ppx + 5, ppy - 5, text=pin_label, anchor="sw", fill="#1d4ed8", font=("Segoe UI", 7))

    def _draw_image_notes(self):
        if not self.image_notes:
            return
        visible_bounds = self._visible_world_bounds()
        margin_mm = max(4.0, 18.0 / max(0.3, self.zoom))
        for note in self.image_notes:
            if self._drag_render_skip("image", note.image_id):
                continue
            bx1, by1, bx2, by2 = self._image_note_bbox(note)
            if bx2 < visible_bounds[0] - margin_mm or bx1 > visible_bounds[2] + margin_mm or by2 < visible_bounds[1] - margin_mm or by1 > visible_bounds[3] + margin_mm:
                continue
            x1, y1 = self.world_to_canvas(note.x_mm, note.y_mm)
            x2, y2 = self.world_to_canvas(bx2, by2)
            photo = self._canvas_photo_for_image_note(note)
            if photo is not None:
                self._canvas_image_refs.append(photo)
                self.canvas.create_image(x1, y1, anchor="nw", image=photo)
            else:
                self.canvas.create_rectangle(x1, y1, x2, y2, fill="#eef2f7", outline="#94a3b8", width=1.5)
                label = Path(note.source_path).name if note.source_path else note.image_id
                self.canvas.create_text((x1 + x2) / 2.0, (y1 + y2) / 2.0, text=label, fill="#475569", font=("Segoe UI", 9))
            if self._is_item_selected("image", note.image_id):
                self.canvas.create_rectangle(x1, y1, x2, y2, outline="#d61f1f", dash=(4, 4), width=2)

    def _draw_text_notes(self):
        if not self.text_notes:
            return
        visible_bounds = self._visible_world_bounds()
        margin_mm = max(4.0, 18.0 / max(0.3, self.zoom))
        for note in self.text_notes:
            if self._drag_render_skip("text", note.note_id):
                continue
            bx1, by1, bx2, by2 = self._text_note_bbox(note)
            if bx2 < visible_bounds[0] - margin_mm or bx1 > visible_bounds[2] + margin_mm or by2 < visible_bounds[1] - margin_mm or by1 > visible_bounds[3] + margin_mm:
                continue
            px, py = self.world_to_canvas(note.x_mm, note.y_mm)
            font_size = max(6, int(round(note.font_size_pt)))
            self.canvas.create_text(px, py, anchor="nw", text=note.text, fill=note.color, font=("Segoe UI", font_size))
            if self._is_item_selected("text", note.note_id):
                sx1, sy1 = self.world_to_canvas(bx1, by1)
                sx2, sy2 = self.world_to_canvas(bx2, by2)
                self.canvas.create_rectangle(sx1, sy1, sx2, sy2, outline="#d61f1f", dash=(4, 4), width=2)

    def _draw_wires(self):
        bridge_specs = self._wire_bridge_specs()
        selected_wire_ids = self._selected_wire_id_set_for_drawing()
        primary_selected_wire_id = self.selected[1] if self.selected and self.selected[0] == "wire" else None

        def draw_polyline(points: List[Tuple[float, float]], color: str, width_px: float, smooth: bool = False):
            if len(points) < 2:
                return
            canvas_points: List[float] = []
            for x, y in points:
                px, py = self.world_to_canvas(x, y)
                canvas_points.extend([px, py])
            self.canvas.create_line(*canvas_points, fill=color, width=width_px, smooth=smooth, capstyle=tk.ROUND, joinstyle=tk.ROUND)

        for w in self.wires:
            if self._drag_render_skip("wire", w.wire_id):
                continue
            if len(w.points_mm) < 2:
                continue
            line_width_px = max(1.0, w.width_mm * self.zoom * 0.22)
            wire_bridges = bridge_specs.get(w.wire_id, [])
            if wire_bridges and self._wire_supports_bridge(w):
                centerline = self._wire_centerline_points(w, curve_samples=60)
                segments = self._polyline_without_ranges(
                    centerline,
                    [(spec["clear_start"], spec["clear_end"]) for spec in wire_bridges],
                )
                for segment in segments:
                    draw_polyline(segment, w.color, line_width_px, smooth=False)
                total_length = self._polyline_length(centerline)
                for spec in wire_bridges:
                    arc_start = clamp(spec["arc_start"], 0.0, total_length)
                    arc_end = clamp(spec["arc_end"], 0.0, total_length)
                    start_point, _start_tangent = self._point_and_tangent_on_polyline(centerline, arc_start)
                    end_point, _end_tangent = self._point_and_tangent_on_polyline(centerline, arc_end)
                    bridge_points = self._bridge_curve_points(start_point, end_point, spec["normal"], spec["height"], samples=20)
                    draw_polyline(bridge_points, w.color, line_width_px, smooth=False)
            else:
                polylines = self._wire_display_polylines(w)
                smooth = bool(normalize_wire_style(w.style) == "curve")
                for idx, line in enumerate(polylines):
                    color = w.color if idx == 0 else w.color_b
                    draw_polyline(line, color, line_width_px, smooth=smooth)
            if w.label:
                mid = self._wire_label_position(w)
                tx, ty = self.world_to_canvas(mid[0], mid[1])
                self.canvas.create_text(tx + 8, ty - 8, text=w.label, anchor="sw", fill="#1f2937", font=("Segoe UI", 8))

            endpoints = self._wire_endpoints(w)
            if w.wire_id in selected_wire_ids and endpoints is not None:
                for x, y in endpoints:
                    px, py = self.world_to_canvas(x, y)
                    if primary_selected_wire_id == w.wire_id:
                        self.canvas.create_oval(px - 6, py - 6, px + 6, py + 6, fill="white", outline="#d61f1f", width=2)
                        self.canvas.create_oval(px - 2, py - 2, px + 2, py + 2, fill="#d61f1f", outline="")
                    else:
                        self.canvas.create_oval(px - 3, py - 3, px + 3, py + 3, fill="#d61f1f", outline="")
            if primary_selected_wire_id == w.wire_id and endpoints is not None and self._wire_has_tangent_handles(w):
                tangent_handles = self._wire_tangent_handle_points(w)
                start_canvas = self.world_to_canvas(endpoints[0][0], endpoints[0][1])
                end_canvas = self.world_to_canvas(endpoints[1][0], endpoints[1][1])
                start_tangent = self.world_to_canvas(tangent_handles["start_tangent"][0], tangent_handles["start_tangent"][1])
                end_tangent = self.world_to_canvas(tangent_handles["end_tangent"][0], tangent_handles["end_tangent"][1])
                self.canvas.create_line(*start_canvas, *start_tangent, fill="#f08c00", dash=(4, 3), width=1)
                self.canvas.create_line(*end_canvas, *end_tangent, fill="#f08c00", dash=(4, 3), width=1)
                for cx, cy in (start_tangent, end_tangent):
                    self.canvas.create_oval(cx - 5, cy - 5, cx + 5, cy + 5, fill="white", outline="#f08c00", width=2)
                    self.canvas.create_oval(cx - 2, cy - 2, cx + 2, cy + 2, fill="#f08c00", outline="")
            if primary_selected_wire_id == w.wire_id and self._wire_has_curve_handle(w):
                if endpoints is not None:
                    mid_x = (endpoints[0][0] + endpoints[1][0]) / 2.0
                    mid_y = (endpoints[0][1] + endpoints[1][1]) / 2.0
                    ctrl_x, ctrl_y = self._curve_control_point(w)
                    mx, my = self.world_to_canvas(mid_x, mid_y)
                    cx, cy = self.world_to_canvas(ctrl_x, ctrl_y)
                    self.canvas.create_line(mx, my, cx, cy, fill="#d61f1f", dash=(4, 3), width=1)
                    self.canvas.create_oval(cx - 6, cy - 6, cx + 6, cy + 6, fill="white", outline="#d61f1f", width=2)
                    self.canvas.create_oval(cx - 2, cy - 2, cx + 2, cy + 2, fill="#d61f1f", outline="")

    def _draw_leaders(self):
        for l in self.leaders:
            if self._drag_render_skip("leader", l.leader_id):
                continue
            path = self._leader_polyline(l)
            canvas_points: List[float] = []
            for x, y in path:
                px, py = self.world_to_canvas(x, y)
                canvas_points.extend([px, py])
            stroke_width_px = max(1.0, l.width_mm * self.zoom * 0.22)
            self.canvas.create_line(*canvas_points, fill=l.color, width=stroke_width_px, capstyle=tk.ROUND, joinstyle=tk.ROUND)

            arrow_canvas: List[float] = []
            for x, y in self._leader_arrow_points(l):
                px, py = self.world_to_canvas(x, y)
                arrow_canvas.extend([px, py])
            self.canvas.create_polygon(*arrow_canvas, fill=l.color, outline=l.color)

            selected = self._is_item_selected("leader", l.leader_id)
            label_bbox = self._leader_text_bbox(l, include_empty=selected)
            if label_bbox:
                bx1, by1 = self.world_to_canvas(label_bbox[0], label_bbox[1])
                bx2, by2 = self.world_to_canvas(label_bbox[2], label_bbox[3])
                if l.text_box:
                    self.canvas.create_rectangle(bx1, by1, bx2, by2, fill="white", outline=l.color, width=1)
                elif selected:
                    self.canvas.create_rectangle(bx1, by1, bx2, by2, fill="", outline="#d61f1f", dash=(3, 3), width=1)
                tx, ty = self.world_to_canvas(label_bbox[0] + 1.2, label_bbox[1] + 0.9)
                self.canvas.create_text(tx, ty, anchor="nw", text=l.text, fill=l.color, font=("Segoe UI", int(round(l.text_size_pt))))

            if selected:
                for x, y in (l.start_mm, l.end_mm):
                    px, py = self.world_to_canvas(x, y)
                    self.canvas.create_oval(px - 6, py - 6, px + 6, py + 6, fill="white", outline="#d61f1f", width=2)
                    self.canvas.create_oval(px - 2, py - 2, px + 2, py + 2, fill="#d61f1f", outline="")

    def _draw_dimensions(self):
        for dim in self.dimensions:
            if self._drag_render_skip("dimension", dim.dim_id):
                continue
            geo = self._dimension_geometry(dim)
            line_width_px = max(1.0, dim.line_width_mm * self.zoom * 0.22)
            f1, f2 = geo["feet"]
            fx1, fy1 = self.world_to_canvas(*f1)
            fx2, fy2 = self.world_to_canvas(*f2)
            self.canvas.create_line(fx1, fy1, fx2, fy2, fill=dim.color, width=line_width_px, capstyle=tk.ROUND)
            for seg_start, seg_end in geo["ext"]:
                sx, sy = self.world_to_canvas(*seg_start)
                ex, ey = self.world_to_canvas(*seg_end)
                self.canvas.create_line(sx, sy, ex, ey, fill=dim.color, width=max(1.0, line_width_px * 0.8))
            for tri in geo["arrows"]:
                pts: List[float] = []
                for px, py in tri:
                    cx, cy = self.world_to_canvas(px, py)
                    pts.extend([cx, cy])
                self.canvas.create_polygon(*pts, fill=dim.color, outline=dim.color)
            tx, ty = self.world_to_canvas(*geo["text_pos"])
            self.canvas.create_text(
                tx,
                ty,
                text=geo["text"],
                fill=dim.color,
                font=("Segoe UI", max(6, int(round(dim.text_size_pt)))),
                anchor="center",
            )
            if self._is_item_selected("dimension", dim.dim_id):
                for point in (dim.p1_mm, dim.p2_mm):
                    px, py = self.world_to_canvas(*point)
                    self.canvas.create_oval(px - 6, py - 6, px + 6, py + 6, fill="white", outline="#d61f1f", width=2)
                    self.canvas.create_oval(px - 2, py - 2, px + 2, py + 2, fill="#d61f1f", outline="")

    def _draw_tables(self):
        for t in self.tables:
            if self._drag_render_skip("table", t.table_id):
                continue
            widths = self._table_col_widths(t)
            heights = self._table_row_heights(t)
            width = sum(widths)
            height = sum(heights)
            x1, y1 = self.world_to_canvas(t.x_mm, t.y_mm)
            x2, y2 = self.world_to_canvas(t.x_mm + width, t.y_mm + height)
            stroke_width_px = max(1.0, t.border_width_mm * self.zoom * 0.2)
            self.canvas.create_rectangle(x1, y1, x2, y2, outline=t.border_color, width=stroke_width_px, fill="")

            cx = t.x_mm
            for col in range(1, t.cols):
                cx += widths[col - 1]
                wx = cx
                px1, py1 = self.world_to_canvas(wx, t.y_mm)
                px2, py2 = self.world_to_canvas(wx, t.y_mm + height)
                self.canvas.create_line(px1, py1, px2, py2, fill=t.border_color, width=stroke_width_px)

            cy = t.y_mm
            for row in range(1, t.rows):
                cy += heights[row - 1]
                wy = cy
                px1, py1 = self.world_to_canvas(t.x_mm, wy)
                px2, py2 = self.world_to_canvas(t.x_mm + width, wy)
                self.canvas.create_line(px1, py1, px2, py2, fill=t.border_color, width=stroke_width_px)

            for r in range(t.rows):
                for c in range(t.cols):
                    txt = ""
                    if r < len(t.cells) and c < len(t.cells[r]):
                        txt = t.cells[r][c]
                    if not txt:
                        continue
                    cell_x, cell_y, cw, ch = self._table_cell_rect(t, r, c)
                    tx, ty, anchor = self._table_text_anchor_position(t, cell_x, cell_y, cw, ch)
                    px, py = self.world_to_canvas(tx, ty)
                    self.canvas.create_text(px, py, anchor=anchor, text=txt, fill="#1f2937", font=("Segoe UI", 8))

            if self._is_item_selected("table", t.table_id):
                self.canvas.create_rectangle(x1, y1, x2, y2, outline="#d61f1f", dash=(4, 4), width=2)

    def _draw_temporary_geometry(self):
        if self.mode == "draw_wire":
            pts = list(self.temp_wire_points)
            if pts:
                pts.append(self.cursor_world)
            if len(pts) >= 2:
                tmp_wire = WirePath(
                    wire_id="__tmp__",
                    points_mm=[pts[0], pts[-1]],
                    color=self.default_wire_color,
                    color_b=self.default_wire_color_b,
                    width_mm=max(0.6, self.default_wire_width_mm),
                    style=self.default_wire_style,
                    curve_offset_mm=self.default_wire_curve_offset_mm,
                    twist_pitch_mm=self.default_wire_twist_pitch_mm,
                    pair_gap_mm=self.default_wire_pair_gap_mm,
                )
                polylines = self._wire_display_polylines(tmp_wire, preview=True)
                smooth = bool(normalize_wire_style(tmp_wire.style) == "curve")
                for idx, line in enumerate(polylines):
                    if len(line) < 2:
                        continue
                    draw = []
                    for x, y in line:
                        px, py = self.world_to_canvas(x, y)
                        draw.extend([px, py])
                    col = "#375a7f" if idx == 0 else "#6b8fb5"
                    self.canvas.create_line(*draw, fill=col, width=2, dash=(6, 4), smooth=smooth, tags=("temp_overlay",))

        if self.mode == "draw_leader" and self.temp_leader_start:
            x1, y1 = self.world_to_canvas(self.temp_leader_start[0], self.temp_leader_start[1])
            x2, y2 = self.world_to_canvas(self.cursor_world[0], self.cursor_world[1])
            elbow_x = x1 + (x2 - x1) * 0.45
            self.canvas.create_line(x1, y1, elbow_x, y1, x2, y2, fill="#3b5b7a", width=1.5, dash=(4, 4), tags=("temp_overlay",))

        if self.mode == "draw_dimension" and self.temp_dimension_start:
            preview = DimensionLine(
                dim_id="__tmp__",
                p1_mm=self.temp_dimension_start,
                p2_mm=self.cursor_world,
                orientation=self.default_dimension_orientation,
                offset_mm=self.default_dimension_offset_mm,
                arrow_size_mm=self.default_dimension_arrow_size_mm,
                text_size_pt=self.default_dimension_text_size_pt,
                tolerance=self.default_dimension_tolerance,
            )
            geo = self._dimension_geometry(preview)
            f1, f2 = geo["feet"]
            fx1, fy1 = self.world_to_canvas(*f1)
            fx2, fy2 = self.world_to_canvas(*f2)
            self.canvas.create_line(fx1, fy1, fx2, fy2, fill="#3b5b7a", width=1.5, dash=(4, 4), tags=("temp_overlay",))
            for seg_start, seg_end in geo["ext"]:
                sx, sy = self.world_to_canvas(*seg_start)
                ex, ey = self.world_to_canvas(*seg_end)
                self.canvas.create_line(sx, sy, ex, ey, fill="#3b5b7a", width=1.0, dash=(3, 3), tags=("temp_overlay",))
            tx, ty = self.world_to_canvas(*geo["text_pos"])
            self.canvas.create_text(tx, ty, text=geo["text"], fill="#3b5b7a", anchor="center", tags=("temp_overlay",))

        if self.mode == "draw_table" and self.temp_table_start:
            x1, y1 = self.world_to_canvas(self.temp_table_start[0], self.temp_table_start[1])
            x2, y2 = self.world_to_canvas(self.cursor_world[0], self.cursor_world[1])
            self.canvas.create_rectangle(x1, y1, x2, y2, outline="#3b5b7a", dash=(4, 4), width=1.5, tags=("temp_overlay",))

        if self.mode == "select" and self.box_select_state:
            start = self.box_select_state.get("start", (0.0, 0.0))
            current = self.box_select_state.get("current", start)
            x1, y1 = self.world_to_canvas(start[0], start[1])
            x2, y2 = self.world_to_canvas(current[0], current[1])
            self.canvas.create_rectangle(x1, y1, x2, y2, outline="#2563eb", dash=(5, 3), width=1.5, tags=("temp_overlay",))

    # ---------------- SVG Export ----------------
    def _svg_connector_lines(self) -> List[str]:
        lines: List[str] = []
        for c in self.connectors:
            world_polylines, _bbox = self._connector_world_geometry(c)
            if not world_polylines:
                continue
            for poly in world_polylines:
                if len(poly) < 2:
                    continue
                coords = [f"{x:.3f},{y:.3f}" for x, y in poly]
                lines.append(
                    f'<polyline points="{" ".join(coords)}" fill="none" stroke="{escape(c.line_color)}" stroke-width="{max(0.15, c.line_width_mm * 0.4):.3f}"/>'
                )
            label_x = c.x_mm + c.label_dx_mm
            label_y = c.y_mm + c.label_dy_mm
            lines.append(f'<text x="{label_x:.3f}" y="{label_y:.3f}" text-anchor="middle" class="txt">{escape(c.connector_id)}</text>')
        return lines

    def _svg_wire_lines(self) -> List[str]:
        out: List[str] = []
        bridge_specs = self._wire_bridge_specs()
        for w in self.wires:
            stroke_w = max(0.2, w.width_mm * 0.4)
            wire_bridges = bridge_specs.get(w.wire_id, [])
            if wire_bridges and self._wire_supports_bridge(w):
                centerline = self._wire_centerline_points(w, curve_samples=60)
                segments = self._polyline_without_ranges(
                    centerline,
                    [(spec["clear_start"], spec["clear_end"]) for spec in wire_bridges],
                )
                for segment in segments:
                    if len(segment) < 2:
                        continue
                    coords = " ".join(f"{x:.3f},{y:.3f}" for x, y in segment)
                    out.append(
                        f'<polyline points="{coords}" fill="none" stroke="{escape(w.color)}" stroke-width="{stroke_w:.3f}" stroke-linecap="round" stroke-linejoin="round"/>'
                    )
                total_length = self._polyline_length(centerline)
                for spec in wire_bridges:
                    start_point, _ = self._point_and_tangent_on_polyline(centerline, clamp(spec["arc_start"], 0.0, total_length))
                    end_point, _ = self._point_and_tangent_on_polyline(centerline, clamp(spec["arc_end"], 0.0, total_length))
                    bridge_points = self._bridge_curve_points(start_point, end_point, spec["normal"], spec["height"], samples=20)
                    bridge_coords = " ".join(f"{x:.3f},{y:.3f}" for x, y in bridge_points)
                    out.append(
                        f'<polyline points="{bridge_coords}" fill="none" stroke="{escape(w.color)}" stroke-width="{stroke_w:.3f}" stroke-linecap="round" stroke-linejoin="round"/>'
                    )
            else:
                polylines = self._wire_display_polylines(w)
                for idx, line in enumerate(polylines):
                    if len(line) < 2:
                        continue
                    pts = " ".join(f"{x:.3f},{y:.3f}" for x, y in line)
                    color = w.color if idx == 0 else w.color_b
                    out.append(
                        f'<polyline points="{pts}" fill="none" stroke="{escape(color)}" stroke-width="{stroke_w:.3f}" stroke-linecap="round" stroke-linejoin="round"/>'
                    )
            if w.label and w.points_mm:
                mx, my = self._wire_label_position(w)
                out.append(f'<text x="{mx + 1.2:.3f}" y="{my - 1.2:.3f}" class="txt">{escape(w.label)}</text>')
        return out

    def _svg_image_note_lines(self) -> List[str]:
        out: List[str] = []
        for note in self.image_notes:
            href = self._svg_image_href(note)
            if not href:
                continue
            width_mm, height_mm = self._image_note_display_size(note)
            out.append(
                f'<image x="{note.x_mm:.3f}" y="{note.y_mm:.3f}" width="{width_mm:.3f}" height="{height_mm:.3f}" '
                f'href="{escape(href, {"\"": "&quot;"})}" preserveAspectRatio="none"/>'
            )
        return out

    def _svg_text_note_lines(self) -> List[str]:
        out: List[str] = []
        for note in self.text_notes:
            out.extend(self._svg_multiline_text(note.x_mm, note.y_mm, note.text, note.color, note.font_size_pt))
        return out

    def _build_svg_text(self) -> str:
        width = self.paper_w_mm
        height = self.paper_h_mm
        f = self.frame_margin_mm

        out = [
            '<?xml version="1.0" encoding="UTF-8"?>',
            f'<svg xmlns="http://www.w3.org/2000/svg" width="{width}mm" height="{height}mm" viewBox="0 0 {width} {height}">',
            '<style>.txt{font-family:Segoe UI,Arial,sans-serif;font-size:3.5px;fill:#172638}</style>',
            f'<rect x="0" y="0" width="{width}" height="{height}" fill="#ffffff" stroke="#7c8aa0" stroke-width="0.8"/>',
            f'<rect x="{f}" y="{f}" width="{width - 2*f}" height="{height - 2*f}" fill="none" stroke="#25364a" stroke-width="0.8"/>',
        ]

        zone = self._zone_marker_drawing()
        for zx1, zy1, zx2, zy2 in zone["lines"]:
            out.append(f'<line x1="{zx1:.3f}" y1="{zy1:.3f}" x2="{zx2:.3f}" y2="{zy2:.3f}" stroke="{escape(zone["color"])}" stroke-width="0.3"/>')
        for t in zone["texts"]:
            out.append(
                f'<text x="{t["x"]:.3f}" y="{t["y"]:.3f}" text-anchor="middle" dominant-baseline="middle" '
                f'font-size="{t["pt"] * 0.3528:.2f}" fill="{escape(zone["color"])}">{escape(t["text"])}</text>'
            )

        title_block = self._title_block_drawing()
        tb_x1, tb_y1, tb_x2, tb_y2 = title_block["rect"]
        out.append(f'<rect x="{tb_x1:.3f}" y="{tb_y1:.3f}" width="{tb_x2 - tb_x1:.3f}" height="{tb_y2 - tb_y1:.3f}" fill="#ffffff" stroke="none"/>')
        for lx1, ly1, lx2, ly2 in title_block["lines"]:
            out.append(f'<line x1="{lx1:.3f}" y1="{ly1:.3f}" x2="{lx2:.3f}" y2="{ly2:.3f}" stroke="{escape(title_block["line_color"])}" stroke-width="0.35"/>')
        for t in title_block["texts"]:
            anchor = "middle" if t["anchor"] == "mm" else "start"
            baseline = "middle" if t["anchor"] == "mm" else "hanging"
            weight = ' font-weight="bold"' if t["bold"] else ""
            out.append(
                f'<text x="{t["x"]:.3f}" y="{t["y"]:.3f}" text-anchor="{anchor}" dominant-baseline="{baseline}" '
                f'font-size="{t["pt"] * 0.3528:.2f}" fill="{escape(title_block["text_color"])}"{weight}>{escape(t["text"])}</text>'
            )

        out.extend(self._svg_image_note_lines())
        out.extend(self._svg_connector_lines())
        out.extend(self._svg_wire_lines())

        for l in self.leaders:
            leader_points = self._leader_polyline(l)
            point_text = " ".join(f"{x:.3f},{y:.3f}" for x, y in leader_points)
            out.append(
                f'<polyline points="{point_text}" fill="none" stroke="{escape(l.color)}" stroke-width="{max(0.15, l.width_mm * 0.4):.3f}"/>'
            )
            arrow_points = " ".join(f"{x:.3f},{y:.3f}" for x, y in self._leader_arrow_points(l))
            out.append(f'<polygon points="{arrow_points}" fill="{escape(l.color)}" stroke="{escape(l.color)}"/>')
            label_bbox = self._leader_text_bbox(l)
            if label_bbox:
                bx1, by1, bx2, by2 = label_bbox
                if l.text_box:
                    out.append(
                        f'<rect x="{bx1:.3f}" y="{by1:.3f}" width="{bx2 - bx1:.3f}" height="{by2 - by1:.3f}" fill="#ffffff" stroke="{escape(l.color)}" stroke-width="0.25"/>'
                    )
                out.extend(self._svg_multiline_text(bx1 + 1.2, by1 + 0.9, l.text, l.color, l.text_size_pt))

        for dim in self.dimensions:
            geo = self._dimension_geometry(dim)
            sw = max(0.12, dim.line_width_mm * 0.4)
            f1, f2 = geo["feet"]
            out.append(f'<line x1="{f1[0]:.3f}" y1="{f1[1]:.3f}" x2="{f2[0]:.3f}" y2="{f2[1]:.3f}" stroke="{escape(dim.color)}" stroke-width="{sw:.3f}"/>')
            for seg_start, seg_end in geo["ext"]:
                out.append(
                    f'<line x1="{seg_start[0]:.3f}" y1="{seg_start[1]:.3f}" x2="{seg_end[0]:.3f}" y2="{seg_end[1]:.3f}" stroke="{escape(dim.color)}" stroke-width="{max(0.1, sw * 0.8):.3f}"/>'
                )
            for tri in geo["arrows"]:
                pts = " ".join(f"{x:.3f},{y:.3f}" for x, y in tri)
                out.append(f'<polygon points="{pts}" fill="{escape(dim.color)}" stroke="{escape(dim.color)}"/>')
            out.append(
                f'<text x="{geo["text_pos"][0]:.3f}" y="{geo["text_pos"][1]:.3f}" text-anchor="middle" dominant-baseline="middle" '
                f'font-size="{dim.text_size_pt * 0.3528:.2f}" fill="{escape(dim.color)}">{escape(geo["text"])}</text>'
            )

        for t in self.tables:
            widths = self._table_col_widths(t)
            heights = self._table_row_heights(t)
            tw = sum(widths)
            th = sum(heights)
            out.append(
                f'<rect x="{t.x_mm:.3f}" y="{t.y_mm:.3f}" width="{tw:.3f}" height="{th:.3f}" fill="none" stroke="{escape(t.border_color)}" stroke-width="{max(0.12, t.border_width_mm * 0.4):.3f}"/>'
            )
            x = t.x_mm
            for col in range(1, t.cols):
                x += widths[col - 1]
                out.append(
                    f'<line x1="{x:.3f}" y1="{t.y_mm:.3f}" x2="{x:.3f}" y2="{t.y_mm + th:.3f}" stroke="{escape(t.border_color)}" stroke-width="{max(0.1, t.border_width_mm * 0.3):.3f}"/>'
                )
            y = t.y_mm
            for row in range(1, t.rows):
                y += heights[row - 1]
                out.append(
                    f'<line x1="{t.x_mm:.3f}" y1="{y:.3f}" x2="{t.x_mm + tw:.3f}" y2="{y:.3f}" stroke="{escape(t.border_color)}" stroke-width="{max(0.1, t.border_width_mm * 0.3):.3f}"/>'
                )
            for r in range(t.rows):
                for c in range(t.cols):
                    txt = ""
                    if r < len(t.cells) and c < len(t.cells[r]):
                        txt = t.cells[r][c]
                    if not txt:
                        continue
                    cell_x, cell_y, cw, ch = self._table_cell_rect(t, r, c)
                    tx, ty, anchor = self._table_text_anchor_position(t, cell_x, cell_y, cw, ch)
                    svg_anchor, baseline = self._svg_anchor_attrs(anchor)
                    out.append(
                        f'<text x="{tx:.3f}" y="{ty:.3f}" text-anchor="{svg_anchor}" dominant-baseline="{baseline}" class="txt">{escape(txt)}</text>'
                    )

        out.extend(self._svg_text_note_lines())
        out.append("</svg>")
        return "\n".join(out)

    def _write_svg(self, path: Path):
        path.write_text(self._build_svg_text(), encoding="utf-8")
