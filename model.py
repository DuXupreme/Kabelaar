"""Datamodel: dataclasses, model-constants en bijbehorende helpers.

Losgetrokken uit kabelboom_tekenstudio.py. Bevat geen Tkinter- of UI-code,
zodat het datamodel apart te testen en te hergebruiken is.
"""

from __future__ import annotations

import csv
import io
import re
from dataclasses import dataclass, field
from typing import Dict, List, Optional, Tuple


PROJECT_SCHEMA_VERSION = 1
DEFAULT_WIRE_BRIDGE_ENABLED = True
DEFAULT_WIRE_BRIDGE_HEIGHT_MM = 2.8
DEFAULT_WIRE_BRIDGE_LENGTH_MM = 8.5
DEFAULT_WIRE_BRIDGE_CLEARANCE_MM = 10.0
WIRE_STYLE_OPTIONS = ["Recht", "Gebogen", "Twisted Pair", "Twisted Pair gebogen"]
WIRE_STYLE_TO_INTERNAL = {
    "Recht": "straight",
    "Gebogen": "curve",
    "Twisted Pair": "twisted_pair",
    "Twisted Pair gebogen": "twisted_pair_curve",
}
WIRE_STYLE_TO_LABEL = {v: k for k, v in WIRE_STYLE_TO_INTERNAL.items()}
VALID_WIRE_STYLES = set(WIRE_STYLE_TO_INTERNAL.values())
WIRE_MOVE_SCOPE_OPTIONS = ["Segment", "Hele lijn"]
WIRE_MOVE_SCOPE_TO_INTERNAL = {
    "Segment": "segment",
    "Hele lijn": "chain",
}
WIRE_MOVE_SCOPE_TO_LABEL = {v: k for k, v in WIRE_MOVE_SCOPE_TO_INTERNAL.items()}
WIRE_ENDPOINT_DRAG_SCOPE_OPTIONS = ["Alleen dit uiteinde", "Aangesloten uiteinden mee"]
WIRE_ENDPOINT_DRAG_SCOPE_TO_INTERNAL = {
    "Alleen dit uiteinde": "single",
    "Alleen dit segment": "single",
    "Aangesloten uiteinden mee": "junction",
    "Hele knoop mee": "junction",
}
WIRE_ENDPOINT_DRAG_SCOPE_TO_LABEL = {
    "single": "Alleen dit uiteinde",
    "junction": "Aangesloten uiteinden mee",
}
PAPER_PRESET_CUSTOM = "Aangepast"
PAPER_PRESET_OPTIONS = [
    "IEC A4 staand",
    "IEC A4 liggend",
    "IEC A3 staand",
    "IEC A3 liggend",
    "IEC A2 staand",
    "IEC A2 liggend",
    "IEC A1 staand",
    "IEC A1 liggend",
    "IEC A0 staand",
    "IEC A0 liggend",
    PAPER_PRESET_CUSTOM,
]
PAPER_PRESET_SIZES_MM = {
    "IEC A4 staand": (210.0, 297.0),
    "IEC A4 liggend": (297.0, 210.0),
    "IEC A3 staand": (297.0, 420.0),
    "IEC A3 liggend": (420.0, 297.0),
    "IEC A2 staand": (420.0, 594.0),
    "IEC A2 liggend": (594.0, 420.0),
    "IEC A1 staand": (594.0, 841.0),
    "IEC A1 liggend": (841.0, 594.0),
    "IEC A0 staand": (841.0, 1189.0),
    "IEC A0 liggend": (1189.0, 841.0),
}
DEFAULT_PAPER_PRESET = "IEC A3 liggend"
DEFAULT_LEADER_ARROW_SIZE_MM = 3.0
DEFAULT_LEADER_TEXT_SIZE_PT = 9.0
DEFAULT_DIMENSION_COLOR = "#1e3a5f"
DEFAULT_DIMENSION_LINE_WIDTH_MM = 0.35
DEFAULT_DIMENSION_ARROW_SIZE_MM = 2.5
DEFAULT_DIMENSION_TEXT_SIZE_PT = 8.0
DEFAULT_DIMENSION_OFFSET_MM = 14.0
DIMENSION_ORIENTATIONS = ("horizontal", "vertical", "aligned")
DIMENSION_ORIENTATION_TO_LABEL = {
    "horizontal": "Horizontaal",
    "vertical": "Verticaal",
    "aligned": "Uitgelijnd",
}
DIMENSION_LABEL_TO_ORIENTATION = {label: key for key, label in DIMENSION_ORIENTATION_TO_LABEL.items()}
DIMENSION_ORIENTATION_OPTIONS = list(DIMENSION_ORIENTATION_TO_LABEL.values())


@dataclass
class StepSymbol:
    name: str
    source_path: str
    projection: str
    polylines: List[List[Tuple[float, float]]]
    width_mm: float
    height_mm: float


@dataclass
class ConnectorInstance:
    connector_id: str
    symbol_name: str
    x_mm: float
    y_mm: float
    scale: float = 1.0
    rotation_deg: float = 0.0
    mirror_x: bool = False
    mirror_y: bool = False
    line_color: str = "#2a3550"
    line_width_mm: float = 0.6
    note: str = ""
    part_number: str = ""
    pin_count: int = 1
    pin_labels: List[str] = field(default_factory=list)
    label_dx_mm: float = 0.0
    label_dy_mm: float = -6.0
    pin_offsets_mm: List[Tuple[float, float]] = field(default_factory=list)


def connector_pin_label(index: int, labels: List[str]) -> str:
    """Label voor pin op positie ``index`` (0-based): expliciet label of het 1-based nummer."""
    if 0 <= index < len(labels) and str(labels[index]).strip():
        return str(labels[index]).strip()
    return str(index + 1)


@dataclass
class WirePath:
    wire_id: str
    points_mm: List[Tuple[float, float]]
    color: str = "#1f4e79"
    color_b: str = "#d7263d"
    width_mm: float = 1.2
    style: str = "straight"  # straight|curve|twisted_pair|twisted_pair_curve
    curve_offset_mm: float = 8.0
    start_handle_offset_mm: Tuple[float, float] = (0.0, 0.0)
    end_handle_offset_mm: Tuple[float, float] = (0.0, 0.0)
    twist_pitch_mm: float = 10.0
    pair_gap_mm: float = 2.8
    label: str = ""
    signal_name: str = ""
    from_connector: str = ""
    from_pin: str = ""
    to_connector: str = ""
    to_pin: str = ""
    cross_section_mm2: float = 0.35
    length_mm: float = 0.0
    shielded: bool = False
    net_name: str = ""


def wire_electrical_kwargs(wire: WirePath) -> dict:
    return {
        "signal_name": wire.signal_name,
        "from_connector": wire.from_connector,
        "from_pin": wire.from_pin,
        "to_connector": wire.to_connector,
        "to_pin": wire.to_pin,
        "cross_section_mm2": wire.cross_section_mm2,
        "length_mm": wire.length_mm,
        "shielded": wire.shielded,
        "net_name": wire.net_name,
    }


def parse_pin_labels(text: str) -> List[str]:
    labels = [part.strip() for part in re.split(r"[,;\n]+", str(text or ""))]
    return [label for label in labels if label]


def pin_labels_text(labels: List[str]) -> str:
    return ", ".join(str(label).strip() for label in labels if str(label).strip())


NETLIST_CSV_HEADER = [
    "Wire ID",
    "Signaal",
    "Net",
    "Van connector",
    "Van pin",
    "Naar connector",
    "Naar pin",
    "Kleur",
    "Kleur B",
    "Doorsnede mm2",
    "Lengte mm",
    "Shielded",
    "Label",
    "Draadtype",
]
BOM_CSV_HEADER = ["Type", "Omschrijving", "Aantal", "Totale lengte mm"]


def wire_netlist_rows(wires: List[WirePath]) -> List[List[object]]:
    rows: List[List[object]] = []
    for wire in sorted(wires, key=lambda w: w.wire_id):
        rows.append(
            [
                wire.wire_id,
                wire.signal_name,
                wire.net_name,
                wire.from_connector,
                wire.from_pin,
                wire.to_connector,
                wire.to_pin,
                wire.color,
                wire.color_b,
                f"{max(0.0, wire.cross_section_mm2):g}",
                f"{max(0.0, wire.length_mm):.1f}",
                "Ja" if wire.shielded else "Nee",
                wire.label,
                wire_style_label(wire.style),
            ]
        )
    return rows


def wire_bom_rows(wires: List[WirePath]) -> List[List[object]]:
    groups: Dict[Tuple[str, str, float, bool], Dict[str, float]] = {}
    for wire in wires:
        color_b = wire.color_b if normalize_wire_style(wire.style) in {"twisted_pair", "twisted_pair_curve"} else ""
        key = (wire.color or "onbekend", color_b, max(0.0, wire.cross_section_mm2), bool(wire.shielded))
        groups.setdefault(key, {"qty": 0, "length": 0.0})
        groups[key]["qty"] += 1
        groups[key]["length"] += max(0.0, wire.length_mm)

    rows: List[List[object]] = []
    for (color, color_b, cross_section, shielded), values in sorted(groups.items()):
        color_text = f"{color}/{color_b}" if color_b else color
        rows.append(
            [
                "Draad",
                f"{color_text}, {cross_section:g} mm2, {'shielded' if shielded else 'unshielded'}",
                int(values["qty"]),
                f"{values['length']:.1f}",
            ]
        )
    return rows


def csv_text(header: List[str], rows: List[List[object]]) -> str:
    buffer = io.StringIO(newline="")
    writer = csv.writer(buffer, delimiter=";")
    writer.writerow(header)
    writer.writerows(rows)
    return buffer.getvalue()


def wire_has_electrical_data(wire: WirePath) -> bool:
    return any(
        [
            wire.signal_name,
            wire.from_connector,
            wire.from_pin,
            wire.to_connector,
            wire.to_pin,
            wire.net_name,
            wire.length_mm > 0,
            wire.shielded,
        ]
    )


STANDARD_CROSS_SECTIONS_MM2 = (
    0.13, 0.22, 0.25, 0.34, 0.35, 0.5, 0.75, 1.0, 1.5, 2.5, 4.0, 6.0, 10.0, 16.0, 25.0, 35.0, 50.0,
)


def is_standard_cross_section(value: float, tol: float = 0.02) -> bool:
    return any(abs(value - std) <= tol for std in STANDARD_CROSS_SECTIONS_MM2)


def wire_electrical_drc(wires: List[WirePath], connector_pin_data: Dict[str, Tuple[int, List[str]]]) -> Tuple[List[str], List[str]]:
    findings: List[str] = []
    warnings: List[str] = []
    endpoint_usage: Dict[Tuple[str, str], List[str]] = {}
    connector_ids = set(connector_pin_data)

    for wire in wires:
        if not wire_has_electrical_data(wire):
            warnings.append(f"[WAARSCHUWING] Draad {wire.wire_id} heeft nog geen elektrische metadata.")
            continue

        if not wire.from_connector or not wire.from_pin or not wire.to_connector or not wire.to_pin:
            warnings.append(f"[WAARSCHUWING] Draad {wire.wire_id} mist connector/pin metadata.")

        if not wire.signal_name and not wire.net_name:
            warnings.append(f"[WAARSCHUWING] Draad {wire.wire_id} mist signaal- of netnaam.")

        endpoints: List[Tuple[str, str, str]] = [
            ("van", wire.from_connector.strip().upper(), wire.from_pin.strip()),
            ("naar", wire.to_connector.strip().upper(), wire.to_pin.strip()),
        ]
        normalized_endpoints: List[Tuple[str, str]] = []
        for label, ref, pin in endpoints:
            if ref and ref not in connector_ids:
                findings.append(f"[FOUT] Draad {wire.wire_id} verwijst naar onbekende {label}-connector {ref}.")
            if pin and re.fullmatch(r"\d+", pin) and int(pin) < 1:
                findings.append(f"[FOUT] Draad {wire.wire_id} heeft ongeldige {label}-pin {pin}.")
            if ref in connector_pin_data and pin:
                pin_count, pin_labels = connector_pin_data[ref]
                if re.fullmatch(r"\d+", pin):
                    pin_index = int(pin)
                    if pin_count > 0 and pin_index > pin_count:
                        findings.append(
                            f"[FOUT] Draad {wire.wire_id} gebruikt {label}-pin {pin}, maar connector {ref} heeft {pin_count} pin(s)."
                        )
                elif pin_labels and pin not in pin_labels:
                    warnings.append(f"[WAARSCHUWING] Draad {wire.wire_id} gebruikt onbekend {label}-pinlabel {ref}:{pin}.")
            if ref and pin:
                endpoint = (ref, pin)
                normalized_endpoints.append(endpoint)
                endpoint_usage.setdefault(endpoint, []).append(wire.wire_id)

        if len(normalized_endpoints) == 2 and normalized_endpoints[0] == normalized_endpoints[1]:
            ref, pin = normalized_endpoints[0]
            findings.append(f"[FOUT] Draad {wire.wire_id} begint en eindigt op dezelfde pin ({ref}:{pin}).")

        if wire.cross_section_mm2 <= 0:
            warnings.append(f"[WAARSCHUWING] Draad {wire.wire_id} mist doorsnede mm2.")
        elif not is_standard_cross_section(wire.cross_section_mm2):
            warnings.append(
                f"[WAARSCHUWING] Draad {wire.wire_id} heeft een niet-standaard doorsnede ({wire.cross_section_mm2:g} mm2)."
            )
        if wire.length_mm <= 0:
            warnings.append(f"[WAARSCHUWING] Draad {wire.wire_id} mist elektrische lengte.")

    for (ref, pin), wire_ids in sorted(endpoint_usage.items()):
        unique_ids = sorted(set(wire_ids))
        if len(unique_ids) > 1:
            warnings.append(f"[WAARSCHUWING] Pin {ref}:{pin} wordt gebruikt door meerdere draden: {', '.join(unique_ids)}.")

    # Niet-aangesloten pins: elke connector-pin die door geen enkele draad gebruikt wordt.
    used_pins = set(endpoint_usage.keys())
    for ref, (pin_count, pin_labels) in sorted(connector_pin_data.items()):
        ref_upper = ref.strip().upper()
        for index in range(max(0, pin_count)):
            pin = connector_pin_label(index, pin_labels)
            if (ref_upper, pin) not in used_pins:
                warnings.append(f"[WAARSCHUWING] Pin {ref}:{pin} is niet aangesloten.")

    return findings, warnings


@dataclass
class Leader:
    leader_id: str
    start_mm: Tuple[float, float]
    end_mm: Tuple[float, float]
    text: str
    color: str = "#1e3a5f"
    width_mm: float = 0.7
    arrow_size_mm: float = DEFAULT_LEADER_ARROW_SIZE_MM
    text_size_pt: float = DEFAULT_LEADER_TEXT_SIZE_PT
    text_box: bool = False


@dataclass
class DimensionLine:
    dim_id: str
    p1_mm: Tuple[float, float]
    p2_mm: Tuple[float, float]
    orientation: str = "horizontal"  # horizontal|vertical|aligned
    offset_mm: float = DEFAULT_DIMENSION_OFFSET_MM
    color: str = DEFAULT_DIMENSION_COLOR
    line_width_mm: float = DEFAULT_DIMENSION_LINE_WIDTH_MM
    arrow_size_mm: float = DEFAULT_DIMENSION_ARROW_SIZE_MM
    text_size_pt: float = DEFAULT_DIMENSION_TEXT_SIZE_PT
    tolerance: str = ""
    override_text: str = ""
    decimals: int = 0


def normalize_dimension_orientation(value: str) -> str:
    return value if value in DIMENSION_ORIENTATIONS else "horizontal"


def dimension_orientation_label(value: str) -> str:
    return DIMENSION_ORIENTATION_TO_LABEL.get(normalize_dimension_orientation(value), "Horizontaal")


def dimension_orientation_internal(label: str) -> str:
    return DIMENSION_LABEL_TO_ORIENTATION.get(label, "horizontal")


@dataclass
class TextNote:
    note_id: str
    x_mm: float
    y_mm: float
    text: str
    color: str = "#1f2937"
    font_size_pt: float = 10.0


@dataclass
class ImageNote:
    image_id: str
    x_mm: float
    y_mm: float
    width_mm: float
    height_mm: float
    scale: float = 1.0
    source_path: str = ""
    image_data_b64: str = ""
    mime_type: str = "image/png"


@dataclass
class TableBox:
    table_id: str
    x_mm: float
    y_mm: float
    cols: int
    rows: int
    cell_w_mm: float
    cell_h_mm: float
    border_color: str = "#25364a"
    border_width_mm: float = 0.5
    col_widths_mm: List[float] = field(default_factory=list)
    row_heights_mm: List[float] = field(default_factory=list)
    text_h_align: str = "center"  # left|center|right
    text_v_align: str = "middle"  # top|middle|bottom
    is_border: bool = False
    cells: List[List[str]] = field(default_factory=list)


def safe_name(text: str, fallback: str) -> str:
    out = re.sub(r"[^A-Za-z0-9._-]+", "_", (text or "").strip()).strip("._")
    return out or fallback


def try_float(value: str, fallback: float = 0.0) -> float:
    try:
        return float(str(value).replace(",", ".").strip())
    except (TypeError, ValueError):
        return fallback


def wire_style_label(style: str) -> str:
    return WIRE_STYLE_TO_LABEL.get(normalize_wire_style(style), "Recht")


def wire_style_internal(label: str) -> str:
    return WIRE_STYLE_TO_INTERNAL.get(label, "straight")


def normalize_wire_style(style: str) -> str:
    return style if style in VALID_WIRE_STYLES else "straight"


def wire_move_scope_label(scope: str) -> str:
    return WIRE_MOVE_SCOPE_TO_LABEL.get(scope, "Hele lijn")


def wire_move_scope_internal(label: str) -> str:
    return WIRE_MOVE_SCOPE_TO_INTERNAL.get(label, "chain")


def wire_endpoint_drag_scope_label(scope: str) -> str:
    return WIRE_ENDPOINT_DRAG_SCOPE_TO_LABEL.get(scope, "Alleen dit segment")


def wire_endpoint_drag_scope_internal(label: str) -> str:
    return WIRE_ENDPOINT_DRAG_SCOPE_TO_INTERNAL.get(label, "single")


def paper_preset_dimensions(label: str) -> Optional[Tuple[float, float]]:
    return PAPER_PRESET_SIZES_MM.get(label)


def paper_preset_for_dimensions(width_mm: float, height_mm: float, tol_mm: float = 0.5) -> str:
    for label, (preset_w, preset_h) in PAPER_PRESET_SIZES_MM.items():
        if abs(width_mm - preset_w) <= tol_mm and abs(height_mm - preset_h) <= tol_mm:
            return label
    return PAPER_PRESET_CUSTOM
