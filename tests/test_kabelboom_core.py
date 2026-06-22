from __future__ import annotations

import sys
import tempfile
import unittest
import math
from dataclasses import asdict
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
if str(ROOT) not in sys.path:
    sys.path.insert(0, str(ROOT))

from kabelboom_tekenstudio import (
    PAPER_PRESET_CUSTOM,
    PROJECT_SCHEMA_VERSION as TEKENSTUDIO_SCHEMA_VERSION,
    ConnectorInstance,
    DimensionLine,
    Leader,
    StepGeometry3D,
    StepSymbol,
    WirePath,
    connector_pin_label,
    dimension_orientation_internal,
    dimension_orientation_label,
    normalize_dimension_orientation,
    BOM_CSV_HEADER,
    NETLIST_CSV_HEADER,
    csv_text,
    HarnessDrawingStudio,
    normalize_wire_style,
    paper_preset_for_dimensions,
    parse_pin_labels,
    pin_labels_text,
    polyline_point_count,
    polyline_segment_count,
    parse_step_geometry,
    parse_step_length_scale,
    circle_arc_points_3d,
    project_step_geometry,
    safe_name,
    try_float,
    wire_style_internal,
    wire_style_label,
    wire_endpoint_drag_scope_internal,
    wire_endpoint_drag_scope_label,
    wire_bom_rows,
    wire_electrical_drc,
    wire_electrical_kwargs,
    wire_has_electrical_data,
    wire_netlist_rows,
)
from model import is_standard_cross_section, STANDARD_CROSS_SECTIONS_MM2
from project_io import write_text_atomic
import app_settings
from ui_scaling import UI_SCALE_LABELS, normalize_ui_scale_percent
from aa_render import PageImageCache, render_viewport_image, screen_render_dpi
from step_kernel import StepMesh, parse_obj_mesh, project_mesh_outline
from PIL import Image


class DummyVar:
    def __init__(self, value):
        self.value = value

    def get(self):
        return self.value


class DummyEvent:
    def __init__(self, state=0):
        self.state = state


class KabelboomStudioHelpersTest(unittest.TestCase):
    def test_project_schema_versions_are_declared(self):
        self.assertGreaterEqual(TEKENSTUDIO_SCHEMA_VERSION, 1)

    def test_numeric_helpers_accept_common_user_input(self):
        self.assertEqual(try_float(" 3,75 "), 3.75)
        self.assertEqual(try_float("niet numeriek", fallback=9.0), 9.0)

    def test_name_helpers_normalize_output(self):
        self.assertEqual(safe_name(" Tekening: Rev A ", "fallback"), "Tekening_Rev_A")
        self.assertEqual(safe_name("", "fallback"), "fallback")

    def test_ui_scale_percent_is_normalized(self):
        self.assertIn("60%", UI_SCALE_LABELS)
        self.assertIn("70%", UI_SCALE_LABELS)
        self.assertIn("100%", UI_SCALE_LABELS)
        self.assertEqual(normalize_ui_scale_percent("125%"), 125)
        self.assertEqual(normalize_ui_scale_percent("10%"), 60)
        self.assertEqual(normalize_ui_scale_percent("250%"), 200)
        self.assertEqual(normalize_ui_scale_percent("abc", fallback=110), 110)


class TekenstudioModelHelpersTest(unittest.TestCase):
    def test_wire_style_mapping_is_stable(self):
        self.assertEqual(wire_style_internal("Twisted Pair gebogen"), "twisted_pair_curve")
        self.assertEqual(wire_style_label("twisted_pair"), "Twisted Pair")
        self.assertEqual(normalize_wire_style("bestaat_niet"), "straight")
        self.assertEqual(wire_endpoint_drag_scope_internal("Hele knoop mee"), "junction")
        self.assertEqual(wire_endpoint_drag_scope_internal("Aangesloten uiteinden mee"), "junction")
        self.assertEqual(wire_endpoint_drag_scope_label("single"), "Alleen dit uiteinde")

    def test_wirepath_carries_electrical_metadata(self):
        wire = WirePath(
            wire_id="W1",
            points_mm=[(0.0, 0.0), (10.0, 0.0)],
            signal_name="CAN_H",
            from_connector="J100",
            from_pin="1",
            to_connector="J101",
            to_pin="2",
            cross_section_mm2=0.35,
            length_mm=250.0,
            shielded=True,
            net_name="CAN",
        )

        payload = asdict(wire)

        self.assertEqual(payload["signal_name"], "CAN_H")
        self.assertEqual(payload["from_connector"], "J100")
        self.assertEqual(payload["to_pin"], "2")
        self.assertTrue(payload["shielded"])
        self.assertEqual(wire_electrical_kwargs(wire)["net_name"], "CAN")

    def test_shift_constrains_wire_endpoint_drag_to_opposite_endpoint_axis(self):
        app = HarnessDrawingStudio.__new__(HarnessDrawingStudio)
        wire = WirePath(wire_id="W001", points_mm=[(10.0, 10.0), (30.0, 20.0)])

        horizontal, constrained = app._wire_endpoint_drag_constrained_point(
            (22.0, 15.0), DummyEvent(state=0x0001), wire, 0
        )
        vertical, constrained_vertical = app._wire_endpoint_drag_constrained_point(
            (28.0, 35.0), DummyEvent(state=0x0001), wire, 0
        )
        free, constrained_free = app._wire_endpoint_drag_constrained_point(
            (22.0, 15.0), DummyEvent(state=0), wire, 0
        )

        self.assertEqual(horizontal, (22.0, 20.0))
        self.assertTrue(constrained)
        self.assertEqual(vertical, (30.0, 35.0))
        self.assertTrue(constrained_vertical)
        self.assertEqual(free, (22.0, 15.0))
        self.assertFalse(constrained_free)

    def test_curved_wire_chain_gets_continuous_tangent_handles(self):
        app = HarnessDrawingStudio.__new__(HarnessDrawingStudio)
        app.wires = [
            WirePath(wire_id="W001", points_mm=[(0.0, 0.0), (10.0, 0.0)], style="curve", curve_offset_mm=8.0),
            WirePath(wire_id="W002", points_mm=[(10.0, 0.0), (10.0, 10.0)], style="curve", curve_offset_mm=8.0),
            WirePath(wire_id="W003", points_mm=[(10.0, 10.0), (20.0, 10.0)], style="curve", curve_offset_mm=8.0),
        ]

        app._smooth_wire_chains_for_style({"W001", "W002", "W003"})

        w1, w2, w3 = app.wires
        self.assertNotEqual(w1.end_handle_offset_mm, (0.0, 0.0))
        self.assertNotEqual(w2.start_handle_offset_mm, (0.0, 0.0))
        self.assertNotEqual(w2.end_handle_offset_mm, (0.0, 0.0))
        self.assertNotEqual(w3.start_handle_offset_mm, (0.0, 0.0))

        first_joint_in = (-w1.end_handle_offset_mm[0], -w1.end_handle_offset_mm[1])
        first_joint_out = w2.start_handle_offset_mm
        cross = first_joint_in[0] * first_joint_out[1] - first_joint_in[1] * first_joint_out[0]
        dot = first_joint_in[0] * first_joint_out[0] + first_joint_in[1] * first_joint_out[1]
        self.assertAlmostEqual(cross, 0.0, places=6)
        self.assertGreater(dot, 0.0)

        self.assertGreater(math.hypot(*first_joint_out), 0.1)

    def test_wire_bridge_settings_control_crossing_arcs(self):
        app = HarnessDrawingStudio.__new__(HarnessDrawingStudio)
        app.mode = "select"
        app.drag_start_world = None
        app.drag_original_items = None
        app.selected = None
        app._wire_centerline_cache = {}
        app._wire_polyline_cache = {}
        app._wire_bridge_signature = None
        app._wire_bridge_cache = {}
        app.wire_bridge_enabled = True
        app.wire_bridge_height_mm = 5.0
        app.wire_bridge_length_mm = 12.0
        app.wire_bridge_clearance_mm = 16.0
        app.wires = [
            WirePath(wire_id="W001", points_mm=[(0.0, 5.0), (10.0, 5.0)], style="straight", width_mm=1.2),
            WirePath(wire_id="W002", points_mm=[(5.0, -5.0), (5.0, 15.0)], style="straight", width_mm=1.2),
        ]

        specs = app._wire_bridge_specs()

        self.assertIn("W002", specs)
        self.assertEqual(len(specs["W002"]), 1)
        spec = specs["W002"][0]
        self.assertAlmostEqual(spec["height"], 5.0)
        self.assertAlmostEqual(spec["arc_end"] - spec["arc_start"], 12.0)
        self.assertAlmostEqual(spec["clear_end"] - spec["clear_start"], 16.0)

        app.wire_bridge_enabled = False
        self.assertEqual(app._wire_bridge_specs(), {})

    def test_connector_instance_carries_pin_metadata(self):
        connector = ConnectorInstance(
            connector_id="J100",
            symbol_name="sym",
            x_mm=0.0,
            y_mm=0.0,
            part_number="DTM06-12SA",
            pin_count=12,
            pin_labels=parse_pin_labels("A1, A2;CAN_H\nCAN_L"),
        )

        payload = asdict(connector)

        self.assertEqual(payload["part_number"], "DTM06-12SA")
        self.assertEqual(payload["pin_count"], 12)
        self.assertEqual(payload["pin_labels"], ["A1", "A2", "CAN_H", "CAN_L"])
        self.assertEqual(pin_labels_text(connector.pin_labels), "A1, A2, CAN_H, CAN_L")

    def test_clear_selection_resets_selection_sets(self):
        app = HarnessDrawingStudio.__new__(HarnessDrawingStudio)
        app.selected = ("wire", "W001")
        app.selected_items = {("wire", "W001"), ("connector", "J001")}
        app.selected_wire_ids = {"W001"}

        app._clear_selection()

        self.assertIsNone(app.selected)
        self.assertEqual(app.selected_items, set())
        self.assertEqual(app.selected_wire_ids, set())

    def test_leader_geometry_uses_arrow_size_and_text_box(self):
        app = HarnessDrawingStudio.__new__(HarnessDrawingStudio)
        leader = Leader(
            leader_id="L001",
            start_mm=(10.0, 20.0),
            end_mm=(40.0, 30.0),
            text="LET OP",
            arrow_size_mm=6.0,
            text_size_pt=14.0,
            text_box=True,
        )
        smaller = Leader(
            leader_id="L002",
            start_mm=(10.0, 20.0),
            end_mm=(40.0, 30.0),
            text="LET OP",
            text_size_pt=8.0,
        )

        arrow_points = app._leader_arrow_points(leader)
        text_bbox = app._leader_text_bbox(leader)
        smaller_bbox = app._leader_text_bbox(smaller)
        world_bbox = app._leader_world_bbox(leader)

        self.assertEqual(arrow_points[0], leader.start_mm)
        self.assertGreater(abs(arrow_points[1][0] - leader.start_mm[0]), 4.0)
        self.assertIsNotNone(text_bbox)
        self.assertIsNotNone(smaller_bbox)
        self.assertGreater(text_bbox[3] - text_bbox[1], smaller_bbox[3] - smaller_bbox[1])
        self.assertTrue(leader.text_box)
        self.assertFalse(smaller.text_box)
        self.assertGreater(text_bbox[0], leader.end_mm[0])
        self.assertLessEqual(world_bbox[0], leader.start_mm[0])
        self.assertGreaterEqual(world_bbox[2], text_bbox[2])

    def test_wire_defaults_can_sync_from_property_panel_before_drawing(self):
        app = HarnessDrawingStudio.__new__(HarnessDrawingStudio)
        app.selected = None
        app.selected_items = set()
        app.default_wire_color = "#111111"
        app.default_wire_color_b = "#222222"
        app.default_wire_width_mm = 1.0
        app.default_wire_style = "straight"
        app.default_wire_curve_offset_mm = 8.0
        app.default_wire_twist_pitch_mm = 10.0
        app.default_wire_pair_gap_mm = 2.8
        app.prop_color_var = DummyVar("#abcdef")
        app.prop_color_b_var = DummyVar("#fedcba")
        app.prop_width_var = DummyVar("2,5")
        app.prop_wire_style_var = DummyVar("Twisted Pair gebogen")
        app.prop_curve_var = DummyVar("12,5")
        app.prop_twist_pitch_var = DummyVar("6")
        app.prop_pair_gap_var = DummyVar("3,2")

        self.assertTrue(app._sync_wire_defaults_from_property_panel())

        self.assertEqual(app.default_wire_color, "#abcdef")
        self.assertEqual(app.default_wire_color_b, "#fedcba")
        self.assertEqual(app.default_wire_width_mm, 2.5)
        self.assertEqual(app.default_wire_style, "twisted_pair_curve")
        self.assertEqual(app.default_wire_curve_offset_mm, 12.5)
        self.assertEqual(app.default_wire_twist_pitch_mm, 6.0)
        self.assertEqual(app.default_wire_pair_gap_mm, 3.2)

    def test_wire_metadata_exports_netlist_and_bom_rows(self):
        wires = [
            WirePath(
                wire_id="W2",
                points_mm=[(0.0, 0.0), (10.0, 0.0)],
                color="#111111",
                cross_section_mm2=0.5,
                length_mm=120.0,
                signal_name="GND",
                from_connector="J100",
                from_pin="2",
                to_connector="J101",
                to_pin="2",
            ),
            WirePath(
                wire_id="W1",
                points_mm=[(0.0, 1.0), (10.0, 1.0)],
                color="#111111",
                cross_section_mm2=0.5,
                length_mm=80.0,
                signal_name="VBAT",
                from_connector="J100",
                from_pin="1",
                to_connector="J101",
                to_pin="1",
            ),
        ]

        netlist_rows = wire_netlist_rows(wires)
        bom_rows = wire_bom_rows(wires)
        netlist_csv = csv_text(NETLIST_CSV_HEADER, netlist_rows)
        bom_csv = csv_text(BOM_CSV_HEADER, bom_rows)

        self.assertEqual(netlist_rows[0][0], "W1")
        self.assertIn("VBAT", netlist_csv)
        self.assertEqual(len(bom_rows), 1)
        self.assertEqual(bom_rows[0][2], 2)
        self.assertIn("200.0", bom_csv)

    def test_wire_electrical_drc_reports_common_metadata_issues(self):
        wires = [
            WirePath(wire_id="W_EMPTY", points_mm=[(0.0, 0.0), (1.0, 0.0)]),
            WirePath(
                wire_id="W_BAD",
                points_mm=[(0.0, 1.0), (1.0, 1.0)],
                signal_name="SIG",
                from_connector="J100",
                from_pin="1",
                to_connector="J999",
                to_pin="0",
                cross_section_mm2=0.0,
                length_mm=0.0,
            ),
            WirePath(
                wire_id="W_DUP",
                points_mm=[(0.0, 2.0), (1.0, 2.0)],
                signal_name="SIG2",
                from_connector="J100",
                from_pin="1",
                to_connector="J101",
                to_pin="2",
                cross_section_mm2=0.35,
                length_mm=50.0,
            ),
        ]

        findings, warnings = wire_electrical_drc(wires, {"J100": (2, []), "J101": (2, [])})

        self.assertFalse(wire_has_electrical_data(wires[0]))
        self.assertTrue(any("onbekende naar-connector J999" in item for item in findings))
        self.assertTrue(any("ongeldige naar-pin 0" in item for item in findings))
        self.assertTrue(any("geen elektrische metadata" in item for item in warnings))
        self.assertTrue(any("mist doorsnede" in item for item in warnings))
        self.assertTrue(any("J100:1" in item and "meerdere draden" in item for item in warnings))

    def test_wire_electrical_drc_checks_pin_count_and_labels(self):
        wires = [
            WirePath(
                wire_id="W_RANGE",
                points_mm=[(0.0, 0.0), (1.0, 0.0)],
                signal_name="SIG",
                from_connector="J100",
                from_pin="3",
                to_connector="J101",
                to_pin="A2",
                cross_section_mm2=0.35,
                length_mm=100.0,
            ),
            WirePath(
                wire_id="W_LABEL",
                points_mm=[(0.0, 1.0), (1.0, 1.0)],
                signal_name="SIG2",
                from_connector="J100",
                from_pin="A9",
                to_connector="J101",
                to_pin="A1",
                cross_section_mm2=0.35,
                length_mm=100.0,
            ),
        ]

        findings, warnings = wire_electrical_drc(wires, {"J100": (2, ["A1", "A2"]), "J101": (4, ["A1", "A2"])})

        self.assertTrue(any("connector J100 heeft 2 pin" in item for item in findings))
        self.assertTrue(any("onbekend van-pinlabel J100:A9" in item for item in warnings))

    def test_connector_pin_label_prefers_explicit_label_then_index(self):
        self.assertEqual(connector_pin_label(0, ["A1", "A2"]), "A1")
        self.assertEqual(connector_pin_label(1, ["A1", "A2"]), "A2")
        self.assertEqual(connector_pin_label(2, ["A1", "A2"]), "3")
        self.assertEqual(connector_pin_label(0, []), "1")

    def test_standard_cross_section_recognises_common_values(self):
        for std in STANDARD_CROSS_SECTIONS_MM2:
            self.assertTrue(is_standard_cross_section(std))
        self.assertFalse(is_standard_cross_section(0.41))

    def test_drc_reports_unconnected_pins_and_nonstandard_cross_section(self):
        wires = [
            WirePath(
                wire_id="W1",
                points_mm=[(0.0, 0.0), (10.0, 0.0)],
                signal_name="SIG",
                from_connector="J1",
                from_pin="1",
                to_connector="J2",
                to_pin="1",
                cross_section_mm2=0.41,
                length_mm=100.0,
            ),
        ]
        findings, warnings = wire_electrical_drc(wires, {"J1": (2, []), "J2": (1, [])})
        # J1 pin 2 is nergens aangesloten; J1/J2 pin 1 wel.
        self.assertTrue(any("Pin J1:2 is niet aangesloten" in item for item in warnings))
        self.assertFalse(any("Pin J1:1 is niet aangesloten" in item for item in warnings))
        self.assertFalse(any("Pin J2:1 is niet aangesloten" in item for item in warnings))
        self.assertTrue(any("niet-standaard doorsnede" in item for item in warnings))

    def test_pin_world_positions_track_connector_position_and_count(self):
        app = HarnessDrawingStudio.__new__(HarnessDrawingStudio)
        app.symbols = {
            "sym": StepSymbol(
                name="sym",
                source_path="",
                projection="Top (XY)",
                polylines=[[(0.0, 0.0), (10.0, 0.0), (10.0, 6.0), (0.0, 6.0), (0.0, 0.0)]],
                width_mm=10.0,
                height_mm=6.0,
            )
        }
        connector = ConnectorInstance(connector_id="J1", symbol_name="sym", x_mm=20.0, y_mm=30.0, scale=1.0, pin_count=3)
        pins = app._connector_pin_world_points(connector)
        self.assertEqual([p[0] for p in pins], ["1", "2", "3"])
        # Verschuif de connector: pins schuiven exact mee.
        connector.x_mm += 5.0
        pins_shifted = app._connector_pin_world_points(connector)
        for (_l0, x0, y0), (_l1, x1, y1) in zip(pins, pins_shifted):
            self.assertAlmostEqual(x1 - x0, 5.0)
            self.assertAlmostEqual(y1 - y0, 0.0)

    def test_derive_netlist_from_geometry_links_endpoints_to_pins(self):
        app = HarnessDrawingStudio.__new__(HarnessDrawingStudio)
        app.symbols = {
            "sym": StepSymbol(
                name="sym",
                source_path="",
                projection="Top (XY)",
                polylines=[[(0.0, 0.0), (10.0, 0.0), (10.0, 6.0), (0.0, 6.0), (0.0, 0.0)]],
                width_mm=10.0,
                height_mm=6.0,
            )
        }
        c1 = ConnectorInstance(connector_id="J1", symbol_name="sym", x_mm=0.0, y_mm=0.0, scale=1.0, pin_count=2)
        c2 = ConnectorInstance(connector_id="J2", symbol_name="sym", x_mm=100.0, y_mm=0.0, scale=1.0, pin_count=2)
        app.connectors = [c1, c2]
        pin_a = app._connector_pin_world_points(c1)[0]  # (label, x, y)
        pin_b = app._connector_pin_world_points(c2)[1]
        wire = WirePath(wire_id="W1", points_mm=[(pin_a[1], pin_a[2]), (pin_b[1], pin_b[2])])
        app.wires = [wire]
        app.selected = None
        app._capture_before_change = lambda: None
        app._commit_change = lambda *a, **k: None
        app.redraw = lambda *a, **k: None
        app.status = lambda *a, **k: None
        changed = app.derive_netlist_from_geometry(tol_mm=1.0, announce=False)
        self.assertEqual(changed, 2)
        self.assertEqual((wire.from_connector, wire.from_pin), ("J1", pin_a[0]))
        self.assertEqual((wire.to_connector, wire.to_pin), ("J2", pin_b[0]))

    def test_paper_preset_detection(self):
        self.assertEqual(paper_preset_for_dimensions(420.0, 297.0), "IEC A3 liggend")
        self.assertEqual(paper_preset_for_dimensions(123.0, 456.0), PAPER_PRESET_CUSTOM)

    def test_dimension_orientation_mapping_is_stable(self):
        self.assertEqual(normalize_dimension_orientation("aligned"), "aligned")
        self.assertEqual(normalize_dimension_orientation("nonsense"), "horizontal")
        for internal in ("horizontal", "vertical", "aligned"):
            self.assertEqual(dimension_orientation_internal(dimension_orientation_label(internal)), internal)

    def test_dimension_geometry_measures_and_formats_value(self):
        app = HarnessDrawingStudio.__new__(HarnessDrawingStudio)

        horizontal = DimensionLine(dim_id="D1", p1_mm=(40.0, 120.0), p2_mm=(180.0, 120.0), orientation="horizontal")
        geo = app._dimension_geometry(horizontal)
        self.assertAlmostEqual(geo["value"], 140.0)
        self.assertEqual(geo["text"], "140")
        # Both arrow feet sit on a single horizontal dimension line above the points.
        f1, f2 = geo["feet"]
        self.assertAlmostEqual(f1[1], f2[1])
        self.assertLess(f1[1], 120.0)

        vertical = DimensionLine(dim_id="D2", p1_mm=(60.0, 80.0), p2_mm=(60.0, 200.0), orientation="vertical")
        self.assertAlmostEqual(app._dimension_geometry(vertical)["value"], 120.0)

    def test_dimension_value_text_uses_tolerance_and_override(self):
        app = HarnessDrawingStudio.__new__(HarnessDrawingStudio)
        dim = DimensionLine(dim_id="D3", p1_mm=(0.0, 0.0), p2_mm=(100.0, 0.0), tolerance="±10")
        self.assertEqual(app._dimension_value_text(dim, 100.0), "100 ±10")
        dim.override_text = "445"
        self.assertEqual(app._dimension_value_text(dim, 100.0), "445 ±10")
        dim.tolerance = ""
        self.assertEqual(app._dimension_value_text(dim, 100.0), "445")

    def test_polyline_complexity_helpers_count_items(self):
        polylines = [[(0.0, 0.0), (1.0, 0.0), (1.0, 1.0)], [(2.0, 2.0)], []]

        self.assertEqual(polyline_point_count(polylines), 4)
        self.assertEqual(polyline_segment_count(polylines), 2)

    def test_step_geometry_parses_edges_and_projects(self):
        step_text = """
#1 = CARTESIAN_POINT('', (0.0, 0.0, 0.0));
#2 = CARTESIAN_POINT('', (20.0, 10.0, 0.0));
#3 = VERTEX_POINT('', #1);
#4 = VERTEX_POINT('', #2);
#5 = EDGE_CURVE('', #3, #4, #99, .T.);
"""
        with tempfile.TemporaryDirectory() as tmp:
            step_path = Path(tmp) / "connector.step"
            step_path.write_text(step_text, encoding="utf-8")

            geometry = parse_step_geometry(step_path, prefer_kernel=False)

        self.assertEqual(len(geometry.polylines), 1)
        self.assertEqual(len(geometry.polylines[0]), 2)

        polylines, width, height = project_step_geometry(geometry, "Top (XY)")
        self.assertEqual(len(polylines), 1)
        self.assertAlmostEqual(width, 20.0)
        self.assertAlmostEqual(height, 10.0)

    def test_empty_projection_gets_fallback_shape(self):
        polylines, width, height = project_step_geometry(StepGeometry3D(polylines=[]), "Top (XY)")

        self.assertGreaterEqual(len(polylines[0]), 2)
        self.assertAlmostEqual(width, 20.0)
        self.assertAlmostEqual(height, 10.0)

    def test_step_length_scale_detects_si_and_inch_units(self):
        millimetre = "#10=( LENGTH_UNIT() NAMED_UNIT(*) SI_UNIT(.MILLI.,.METRE.) );"
        metre = "#10=( LENGTH_UNIT() NAMED_UNIT(*) SI_UNIT($,.METRE.) );"
        inch = (
            "#10=( LENGTH_UNIT() NAMED_UNIT(*) SI_UNIT(.MILLI.,.METRE.) );\n"
            "#12=LENGTH_MEASURE_WITH_UNIT(LENGTH_MEASURE(25.4),#10);\n"
            "#13=( CONVERSION_BASED_UNIT('INCH',#12) NAMED_UNIT(#11) LENGTH_UNIT() );"
        )
        self.assertAlmostEqual(parse_step_length_scale(millimetre), 1.0)
        self.assertAlmostEqual(parse_step_length_scale(metre), 1000.0)
        self.assertAlmostEqual(parse_step_length_scale(inch), 25.4)
        self.assertAlmostEqual(parse_step_length_scale("geen eenheid hier"), 1.0)

    def test_step_geometry_applies_unit_scale_to_coordinates(self):
        step_text = """
#10=( LENGTH_UNIT() NAMED_UNIT(*) SI_UNIT($,.METRE.) );
#1 = CARTESIAN_POINT('', (0.0, 0.0, 0.0));
#2 = CARTESIAN_POINT('', (0.02, 0.01, 0.0));
#3 = VERTEX_POINT('', #1);
#4 = VERTEX_POINT('', #2);
#5 = EDGE_CURVE('', #3, #4, #99, .T.);
"""
        with tempfile.TemporaryDirectory() as tmp:
            step_path = Path(tmp) / "metre.step"
            step_path.write_text(step_text, encoding="utf-8")
            geometry = parse_step_geometry(step_path, prefer_kernel=False)

        # 0.02 m / 0.01 m -> 20 mm / 10 mm
        _polylines, width, height = project_step_geometry(geometry, "Top (XY)")
        self.assertAlmostEqual(width, 20.0)
        self.assertAlmostEqual(height, 10.0)

    def test_step_circle_edge_is_tessellated_on_radius(self):
        step_text = """
#10=( LENGTH_UNIT() NAMED_UNIT(*) SI_UNIT(.MILLI.,.METRE.) );
#1 = CARTESIAN_POINT('', (0.0, 0.0, 0.0));
#2 = DIRECTION('', (0.0, 0.0, 1.0));
#3 = DIRECTION('', (1.0, 0.0, 0.0));
#4 = AXIS2_PLACEMENT_3D('', #1, #2, #3);
#5 = CIRCLE('', #4, 10.0);
#6 = CARTESIAN_POINT('', (10.0, 0.0, 0.0));
#7 = CARTESIAN_POINT('', (0.0, 10.0, 0.0));
#8 = VERTEX_POINT('', #6);
#9 = VERTEX_POINT('', #7);
#20 = EDGE_CURVE('', #8, #9, #5, .T.);
"""
        with tempfile.TemporaryDirectory() as tmp:
            step_path = Path(tmp) / "arc.step"
            step_path.write_text(step_text, encoding="utf-8")
            geometry = parse_step_geometry(step_path, prefer_kernel=False)

        unique_points = {pt for line in geometry.polylines for pt in line}
        # Een rechte koorde zou maar 2 punten geven; een boog levert er meer.
        self.assertGreater(len(unique_points), 3)
        # Alle punten liggen op de cirkel met straal 10 rond de oorsprong.
        for x, y, z in unique_points:
            self.assertAlmostEqual(math.hypot(x, y), 10.0, places=4)
            self.assertAlmostEqual(z, 0.0, places=6)

    def test_circle_arc_points_close_full_circle_when_endpoints_coincide(self):
        pts = circle_arc_points_3d(
            (0.0, 0.0, 0.0), (0.0, 0.0, 1.0), (1.0, 0.0, 0.0), 5.0, (5.0, 0.0, 0.0), (5.0, 0.0, 0.0)
        )
        self.assertGreater(len(pts), 8)
        for x, y, _z in pts:
            self.assertAlmostEqual(math.hypot(x, y), 5.0, places=6)


class Batch7RenderingAndKernelTest(unittest.TestCase):
    def test_aa_viewport_reuses_cached_page_until_content_changes(self):
        calls = []

        def render_page(dpi):
            calls.append(dpi)
            return Image.new("RGBA", (200, 100), "white")

        cache = PageImageCache()
        first = render_viewport_image(
            render_page,
            cache,
            content_signature="scene-a",
            paper_width_mm=20.0,
            paper_height_mm=10.0,
            canvas_width_px=100,
            canvas_height_px=70,
            zoom_px_per_mm=4.0,
            pan_x_px=10.0,
            pan_y_px=5.0,
        )
        second = render_viewport_image(
            render_page,
            cache,
            content_signature="scene-a",
            paper_width_mm=20.0,
            paper_height_mm=10.0,
            canvas_width_px=100,
            canvas_height_px=70,
            zoom_px_per_mm=4.0,
            pan_x_px=20.0,
            pan_y_px=5.0,
            sharp=False,
        )
        render_viewport_image(
            render_page,
            cache,
            content_signature="scene-b",
            paper_width_mm=20.0,
            paper_height_mm=10.0,
            canvas_width_px=100,
            canvas_height_px=70,
            zoom_px_per_mm=4.0,
            pan_x_px=20.0,
            pan_y_px=5.0,
        )

        self.assertEqual(first.size, (100, 70))
        self.assertEqual(second.size, (100, 70))
        self.assertEqual(len(calls), 2)
        self.assertGreaterEqual(screen_render_dpi(420.0, 297.0, 2.5), 72.0)

    def test_obj_parser_triangulates_faces(self):
        obj_text = "\n".join(
            [
                "v 0 0 0",
                "v 10 0 0",
                "v 10 5 0",
                "v 0 5 0",
                "f 1 2 3 4",
            ]
        )
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / "mesh.obj"
            path.write_text(obj_text, encoding="utf-8")
            mesh = parse_obj_mesh(path)

        self.assertEqual(len(mesh.vertices), 4)
        self.assertEqual(mesh.triangles, [(0, 1, 2), (0, 2, 3)])

    def test_kernel_mesh_projection_returns_cube_outline_without_face_diagonals(self):
        vertices = [
            (0.0, 0.0, 0.0), (10.0, 0.0, 0.0), (10.0, 20.0, 0.0), (0.0, 20.0, 0.0),
            (0.0, 0.0, 5.0), (10.0, 0.0, 5.0), (10.0, 20.0, 5.0), (0.0, 20.0, 5.0),
        ]
        triangles = [
            (0, 2, 1), (0, 3, 2), (4, 5, 6), (4, 6, 7),
            (0, 1, 5), (0, 5, 4), (1, 2, 6), (1, 6, 5),
            (2, 3, 7), (2, 7, 6), (3, 0, 4), (3, 4, 7),
        ]

        outline = project_mesh_outline(StepMesh(vertices, triangles), "Top (XY)")
        points = {point for line in outline for point in line}

        self.assertEqual(points, {(0.0, 0.0), (10.0, 0.0), (10.0, 20.0), (0.0, 20.0)})
        self.assertEqual(len(outline), 4)


class ProjectIoTest(unittest.TestCase):
    def test_atomic_write_creates_backup_when_overwriting(self):
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / "project.json"
            write_text_atomic(path, '{"old": true}')

            backup_path = write_text_atomic(path, '{"new": true}')

            self.assertEqual(path.read_text(encoding="utf-8"), '{"new": true}')
            self.assertIsNotNone(backup_path)
            self.assertTrue(backup_path.exists())
            self.assertEqual(backup_path.read_text(encoding="utf-8"), '{"old": true}')


class AppSettingsTest(unittest.TestCase):
    def test_app_settings_roundtrip(self):
        original_path = app_settings.SETTINGS_PATH
        with tempfile.TemporaryDirectory() as tmp:
            app_settings.SETTINGS_PATH = Path(tmp) / "settings.json"
            try:
                updated = app_settings.update_app_settings(
                    "test_app",
                    ui_scale_percent=125,
                    last_project_dir=tmp,
                )
                loaded = app_settings.load_app_settings("test_app")

                self.assertEqual(updated["ui_scale_percent"], 125)
                self.assertEqual(loaded["last_project_dir"], tmp)
                self.assertEqual(app_settings.existing_dir(tmp), tmp)
                self.assertEqual(app_settings.parent_dir(Path(tmp) / "project.json"), tmp)
            finally:
                app_settings.SETTINGS_PATH = original_path


if __name__ == "__main__":
    unittest.main()
