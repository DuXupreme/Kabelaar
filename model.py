"""Datamodel: dataclasses, model-constants en bijbehorende helpers.

Losgetrokken uit kabelboom_tekenstudio.py. Bevat geen Tkinter- of UI-code,
zodat het datamodel apart te testen en te hergebruiken is.
"""

from __future__ import annotations

import copy
import csv
import io
import re
from dataclasses import dataclass, field
from typing import Dict, List, Optional, Tuple


PROJECT_SCHEMA_VERSION = 3
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
# Knooppunten (Batch 8.2): verbindingspunten die geen connector-pin zijn —
# splices, massapunten en ringterminals. Een draadeinde kan op een knoop landen
# i.p.v. op een connector-pin.
NODE_KIND_OPTIONS = ["Splice", "Massapunt", "Ringterminal", "Algemeen"]
NODE_KIND_TO_INTERNAL = {
    "Splice": "splice",
    "Massapunt": "ground",
    "Ringterminal": "ring",
    "Algemeen": "generic",
}
NODE_KIND_TO_LABEL = {v: k for k, v in NODE_KIND_TO_INTERNAL.items()}
VALID_NODE_KINDS = set(NODE_KIND_TO_INTERNAL.values())
DEFAULT_NODE_KIND = "splice"
NODE_RADIUS_MM = 1.9  # visuele straal van een knoop-glyph (gedeeld door render + hit-test)
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
    mating_part_numbers: List[str] = field(default_factory=list)
    terminal_part_numbers: List[str] = field(default_factory=list)
    accessory_part_numbers: List[str] = field(default_factory=list)
    pin_count: int = 1
    pin_labels: List[str] = field(default_factory=list)
    pin_names: Dict[str, str] = field(default_factory=dict)
    pin_connections: Dict[str, str] = field(default_factory=dict)
    label_dx_mm: float = 0.0
    label_dy_mm: float = -6.0
    pin_offsets_mm: List[Tuple[float, float]] = field(default_factory=list)


def connector_pin_label(index: int, labels: List[str]) -> str:
    """Label voor pin op positie ``index`` (0-based): expliciet label of het 1-based nummer."""
    if 0 <= index < len(labels) and str(labels[index]).strip():
        return str(labels[index]).strip()
    return str(index + 1)


@dataclass
class Node:
    """Verbindingsknoop die geen connector-pin is: splice, massapunt of ringterminal.

    Draadeinden verwijzen via ``WirePath.from_node`` / ``to_node`` naar de ``node_id``.
    Een splice mag bewust meerdere draden samenbrengen; massa-/ringknopen idem.
    """

    node_id: str
    kind: str = DEFAULT_NODE_KIND  # splice|ground|ring|generic
    x_mm: float = 0.0
    y_mm: float = 0.0
    label: str = ""
    color: str = "#2a3550"
    label_dx_mm: float = 0.0  # naam-offset t.o.v. de knoop (blijft mee bij verplaatsen)
    label_dy_mm: float = -4.0


def normalize_node_kind(kind: str) -> str:
    return kind if kind in VALID_NODE_KINDS else "generic"


def node_kind_label(kind: str) -> str:
    return NODE_KIND_TO_LABEL.get(normalize_node_kind(kind), "Algemeen")


def node_kind_internal(label: str) -> str:
    return NODE_KIND_TO_INTERNAL.get(label, "generic")


# --- Projectschema-migratie (Batch 8.1) ------------------------------------
# Eén keten van stapsgewijze migraties brengt een ingeladen project-dict naar
# het huidige PROJECT_SCHEMA_VERSION, zodat de loader altijd de actuele vorm mag
# aannemen. Voeg per nieuwe schemaversie één _migrate_vN_to_vN_plus_1 toe en
# registreer die in _PROJECT_MIGRATIONS, gesleuteld op de bronversie.

def _migrate_v1_to_v2(data: dict) -> dict:
    """v1 → v2: knooppunten (splice/massa/ring) geïntroduceerd.

    v1-projecten kennen geen knopen en draden hebben geen ``from_node``/``to_node``.
    We garanderen de v2-vorm: een top-level ``nodes``-lijst en node-velden op elke draad.
    """
    data.setdefault("nodes", [])
    for wire in data.get("wires", []):
        if isinstance(wire, dict):
            wire.setdefault("from_node", "")
            wire.setdefault("to_node", "")
    return data


def _migrate_v2_to_v3(data: dict) -> dict:
    """v2 -> v3: connector-BOM en pinfunctie/connectie metadata toegevoegd."""
    for connector in data.get("connectors", []):
        if isinstance(connector, dict):
            connector.setdefault("mating_part_numbers", [])
            connector.setdefault("terminal_part_numbers", [])
            connector.setdefault("accessory_part_numbers", [])
            connector.setdefault("pin_names", {})
            connector.setdefault("pin_connections", {})
    return data


_PROJECT_MIGRATIONS = {
    1: _migrate_v1_to_v2,
    2: _migrate_v2_to_v3,
}


def migrate_project_dict(data: dict) -> dict:
    """Werk een ingeladen project-dict bij naar ``PROJECT_SCHEMA_VERSION``.

    Idempotent en niet-destructief voor de aanroeper (werkt op een diepe kopie):
    een al-actuele dict komt inhoudelijk ongewijzigd terug. Een ontbrekende of
    onleesbare ``schema_version`` wordt als v1 behandeld — de enige eerder
    uitgebrachte versie.
    """
    if not isinstance(data, dict):
        return data
    data = copy.deepcopy(data)
    try:
        version = int(data.get("schema_version", 1))
    except (TypeError, ValueError):
        version = 1
    while version < PROJECT_SCHEMA_VERSION:
        migrate = _PROJECT_MIGRATIONS.get(version)
        if migrate is None:
            break
        data = migrate(data)
        version += 1
    data["schema_version"] = PROJECT_SCHEMA_VERSION
    return data


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
    from_node: str = ""  # node_id als dit uiteinde op een splice/massa/ring landt i.p.v. een connector-pin
    to_node: str = ""


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
        "from_node": wire.from_node,
        "to_node": wire.to_node,
    }


def parse_pin_labels(text: str) -> List[str]:
    labels = [part.strip() for part in re.split(r"[,;\n]+", str(text or ""))]
    return [label for label in labels if label]


def pin_labels_text(labels: List[str]) -> str:
    return ", ".join(str(label).strip() for label in labels if str(label).strip())


def parse_part_numbers(text: str) -> List[str]:
    """Lees een compacte partnummer-lijst uit losse regels, komma's of puntkomma's."""
    parts = [part.strip() for part in re.split(r"[,;\n]+", str(text or ""))]
    return [part for part in parts if part]


def part_numbers_text(parts: List[str]) -> str:
    return "\n".join(str(part).strip() for part in parts if str(part).strip())


def _clean_text_map(values) -> Dict[str, str]:
    if not isinstance(values, dict):
        return {}
    out: Dict[str, str] = {}
    for key, value in values.items():
        clean_key = str(key).strip()
        clean_value = str(value).strip()
        if clean_key and clean_value:
            out[clean_key] = clean_value
    return out


def parse_connector_pin_table(text: str) -> Tuple[Dict[str, str], Dict[str, str]]:
    """Parseer regels als ``pin; benaming; connectie`` naar twee pin-maps."""
    pin_names: Dict[str, str] = {}
    pin_connections: Dict[str, str] = {}
    for raw_line in str(text or "").splitlines():
        line = raw_line.strip()
        if not line:
            continue
        parts = re.split(r"\s*[;\t|]\s*", line, maxsplit=2)
        if len(parts) == 1:
            parts = [part.strip() for part in line.split(",", 2)]
        pin = parts[0].strip() if parts else ""
        if not pin:
            continue
        name = parts[1].strip() if len(parts) > 1 else ""
        connection = parts[2].strip() if len(parts) > 2 else ""
        if name:
            pin_names[pin] = name
        if connection:
            pin_connections[pin] = connection
    return pin_names, pin_connections


def connector_pin_table_text(connector: ConnectorInstance) -> str:
    pin_names = _clean_text_map(getattr(connector, "pin_names", {}))
    pin_connections = _clean_text_map(getattr(connector, "pin_connections", {}))
    base_pins = [connector_pin_label(index, connector.pin_labels) for index in range(max(1, connector.pin_count))]
    extra_pins = sorted((set(pin_names) | set(pin_connections)) - set(base_pins))
    lines = []
    for pin in base_pins + extra_pins:
        name = pin_names.get(pin, "")
        connection = pin_connections.get(pin, "")
        if name or connection or pin in base_pins:
            lines.append(f"{pin}; {name}; {connection}")
    return "\n".join(lines)


def _parse_part_number_quantity(raw: str) -> Tuple[str, float]:
    """Ondersteun ``PN x2`` of ``2x PN`` voor vaste accessoire-aantallen."""
    text = str(raw or "").strip()
    if not text:
        return "", 0.0
    qty_first = re.fullmatch(r"(?P<qty>\d+(?:[.,]\d+)?)\s*[xX]\s+(?P<part>.+)", text)
    qty_last = re.fullmatch(r"(?P<part>.+?)(?:\s+[xX]\s*|\s*[xX]\s+)(?P<qty>\d+(?:[.,]\d+)?)", text)
    match = qty_first or qty_last
    if not match:
        return text, 1.0
    part = match.group("part").strip()
    try:
        qty = float(match.group("qty").replace(",", "."))
    except (TypeError, ValueError):
        qty = 1.0
    return part, max(0.0, qty)


def _connector_wire_refs(connector: ConnectorInstance, wires: List["WirePath"]) -> Dict[str, List[Tuple["WirePath", str, str]]]:
    connector_id = connector.connector_id.strip().upper()
    refs: Dict[str, List[Tuple["WirePath", str, str]]] = {}
    for wire in wires:
        from_ref = wire.from_connector.strip().upper()
        to_ref = wire.to_connector.strip().upper()
        if from_ref == connector_id and wire.from_pin.strip():
            other_ref = wire.to_node or wire.to_connector
            other_pin = "" if wire.to_node else wire.to_pin
            refs.setdefault(wire.from_pin.strip(), []).append((wire, other_ref, other_pin))
        if to_ref == connector_id and wire.to_pin.strip():
            other_ref = wire.from_node or wire.from_connector
            other_pin = "" if wire.from_node else wire.from_pin
            refs.setdefault(wire.to_pin.strip(), []).append((wire, other_ref, other_pin))
    return refs


CONNECTOR_PINOUT_CSV_HEADER = [
    "Connector",
    "Connector part",
    "Pin",
    "Benaming",
    "Connectie",
    "Draad ID",
    "Signaal",
    "Net",
    "Tegenconnector/knoop",
    "Tegenpin",
]
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


def connector_pinout_rows(connectors: List[ConnectorInstance], wires: List[WirePath]) -> List[List[object]]:
    rows: List[List[object]] = []
    for connector in sorted(connectors, key=lambda c: c.connector_id):
        pin_names = _clean_text_map(getattr(connector, "pin_names", {}))
        pin_connections = _clean_text_map(getattr(connector, "pin_connections", {}))
        wire_refs = _connector_wire_refs(connector, wires)
        base_pins = [connector_pin_label(index, connector.pin_labels) for index in range(max(1, connector.pin_count))]
        extra_pins = sorted((set(pin_names) | set(pin_connections) | set(wire_refs)) - set(base_pins))
        for pin in base_pins + extra_pins:
            refs = wire_refs.get(pin, [])
            if not refs:
                rows.append(
                    [
                        connector.connector_id,
                        connector.part_number,
                        pin,
                        pin_names.get(pin, ""),
                        pin_connections.get(pin, ""),
                        "",
                        "",
                        "",
                        "",
                        "",
                    ]
                )
                continue
            for wire, other_ref, other_pin in refs:
                derived_connection = f"{other_ref}:{other_pin}" if other_pin else other_ref
                rows.append(
                    [
                        connector.connector_id,
                        connector.part_number,
                        pin,
                        pin_names.get(pin, ""),
                        pin_connections.get(pin, "") or derived_connection,
                        wire.wire_id,
                        wire.signal_name,
                        wire.net_name,
                        other_ref,
                        other_pin,
                    ]
                )
    return rows


def wire_netlist_rows(wires: List[WirePath]) -> List[List[object]]:
    rows: List[List[object]] = []
    for wire in sorted(wires, key=lambda w: w.wire_id):
        # Een draadeinde dat op een knoop (splice/massa/ring) landt, toont de
        # node-id in de connector-kolom; een knoop heeft geen pin.
        from_ref = wire.from_node or wire.from_connector
        from_pin = "" if wire.from_node else wire.from_pin
        to_ref = wire.to_node or wire.to_connector
        to_pin = "" if wire.to_node else wire.to_pin
        rows.append(
            [
                wire.wire_id,
                wire.signal_name,
                wire.net_name,
                from_ref,
                from_pin,
                to_ref,
                to_pin,
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


def connector_populated_pin_count(connector: ConnectorInstance, wires: List[WirePath]) -> int:
    used_pins = set(_connector_wire_refs(connector, wires))
    pin_names = _clean_text_map(getattr(connector, "pin_names", {}))
    pin_connections = _clean_text_map(getattr(connector, "pin_connections", {}))
    for pin in set(pin_names) | set(pin_connections):
        if pin_names.get(pin) or pin_connections.get(pin):
            used_pins.add(pin)
    return len(used_pins)


def _format_quantity(value: float):
    if abs(value - round(value)) < 1e-9:
        return int(round(value))
    return f"{value:g}"


def connector_bom_rows(connectors: List[ConnectorInstance], wires: List[WirePath]) -> List[List[object]]:
    groups: Dict[Tuple[str, str], Dict[str, object]] = {}

    def add_part(kind: str, part_text: str, qty: float, connector_id: str):
        part_number, entry_qty = _parse_part_number_quantity(part_text)
        total_qty = qty * entry_qty
        if not part_number or total_qty <= 0:
            return
        key = (kind, part_number)
        group = groups.setdefault(key, {"qty": 0.0, "refs": []})
        group["qty"] = float(group["qty"]) + total_qty
        refs = group["refs"]
        if isinstance(refs, list) and connector_id not in refs:
            refs.append(connector_id)

    for connector in connectors:
        connector_id = connector.connector_id
        add_part("Connector", connector.part_number, 1.0, connector_id)
        for part in getattr(connector, "mating_part_numbers", []):
            add_part("Mating connector", part, 1.0, connector_id)
        populated_pin_count = connector_populated_pin_count(connector, wires)
        for part in getattr(connector, "terminal_part_numbers", []):
            add_part("Terminal", part, float(populated_pin_count), connector_id)
        for part in getattr(connector, "accessory_part_numbers", []):
            add_part("Accessoire", part, 1.0, connector_id)

    rows: List[List[object]] = []
    for (kind, part_number), values in sorted(groups.items()):
        refs = values.get("refs", [])
        ref_text = f" ({', '.join(refs)})" if isinstance(refs, list) and refs else ""
        rows.append([kind, f"{part_number}{ref_text}", _format_quantity(float(values["qty"])), ""])
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
            wire.from_node,
            wire.to_node,
            wire.length_mm > 0,
            wire.shielded,
        ]
    )


STANDARD_CROSS_SECTIONS_MM2 = (
    0.13, 0.22, 0.25, 0.34, 0.35, 0.5, 0.75, 1.0, 1.5, 2.5, 4.0, 6.0, 10.0, 16.0, 25.0, 35.0, 50.0,
)


def is_standard_cross_section(value: float, tol: float = 0.02) -> bool:
    return any(abs(value - std) <= tol for std in STANDARD_CROSS_SECTIONS_MM2)


def wire_electrical_drc(
    wires: List[WirePath],
    connector_pin_data: Dict[str, Tuple[int, List[str]]],
    node_kinds: Optional[Dict[str, str]] = None,
) -> Tuple[List[str], List[str]]:
    findings: List[str] = []
    warnings: List[str] = []
    endpoint_usage: Dict[Tuple[str, str], List[str]] = {}
    connector_ids = set(connector_pin_data)
    node_ids = set(node_kinds or {})

    for wire in wires:
        if not wire_has_electrical_data(wire):
            warnings.append(f"[WAARSCHUWING] Draad {wire.wire_id} heeft nog geen elektrische metadata.")
            continue

        from_node = wire.from_node.strip()
        to_node = wire.to_node.strip()
        from_ok = bool(from_node) or (bool(wire.from_connector.strip()) and bool(wire.from_pin.strip()))
        to_ok = bool(to_node) or (bool(wire.to_connector.strip()) and bool(wire.to_pin.strip()))
        if not (from_ok and to_ok):
            warnings.append(f"[WAARSCHUWING] Draad {wire.wire_id} mist connector/pin metadata.")

        if not wire.signal_name and not wire.net_name:
            warnings.append(f"[WAARSCHUWING] Draad {wire.wire_id} mist signaal- of netnaam.")

        endpoints: List[Tuple[str, str, str, str]] = [
            ("van", from_node, wire.from_connector.strip().upper(), wire.from_pin.strip()),
            ("naar", to_node, wire.to_connector.strip().upper(), wire.to_pin.strip()),
        ]
        normalized_endpoints: List[Tuple[str, str]] = []
        for label, node_ref, ref, pin in endpoints:
            if node_ref:
                # Knoop-uiteinde (splice/massa/ring): valideer dat de knoop bestaat.
                # Een knoop verbindt bewust meerdere draden, dus géén dubbel-gebruik-telling.
                if node_ref not in node_ids:
                    findings.append(f"[FOUT] Draad {wire.wire_id} verwijst naar onbekende {label}-knoop {node_ref}.")
                normalized_endpoints.append(("node", node_ref))
                continue
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
            first, second = normalized_endpoints[0]
            if first == "node":
                findings.append(f"[FOUT] Draad {wire.wire_id} begint en eindigt op dezelfde knoop ({second}).")
            else:
                findings.append(f"[FOUT] Draad {wire.wire_id} begint en eindigt op dezelfde pin ({first}:{second}).")

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
