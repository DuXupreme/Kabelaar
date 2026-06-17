# Verbeterplan — Kabelboom Tekenstudio

Uitvoerplan in 5 batches. Volgorde is bewust: elke batch is op zichzelf afrondbaar
en levert iets zichtbaars op. Doe na **elke** batch `python -m unittest discover -s tests`
en een korte handmatige rooktest van de app.

Prioriteit-legenda: 🔴 hoog · 🟠 midden · 🟢 laag

---

## Batch 1 — Visuele upgrade (quick wins, laag risico) 🔴 ✅ GEDAAN

**Doel:** de app ziet er meteen modern en native-Windows uit, en kleuren zitten op één plek.

> Uitgevoerd: `sv-ttk`-thema toegevoegd, kleuren naar `theme.py`, licht/donker-toggle in
> `Beeld ▸ Thema`, tooltip-helper (`ui_tooltip.py`) op de cryptische knoppen, dubbele
> mode-knoppen uit het linkerpaneel gehaald, actieve modus met accentstijl. Alle 25 tests groen.

| # | Taak | Bestand(en) | Klaar als… |
|---|------|-------------|------------|
| 1.1 | `sv-ttk` toevoegen aan dependencies | `requirements.txt` | pakket installeert |
| 1.2 | Thema activeren bij opstart (`sv_ttk.set_theme("light")`) | `kabelboom_tekenstudio.py` (`_configure_app_style`, ~regel 1255) | UI rendert in Win11-stijl |
| 1.3 | Alle hardcoded hex-kleuren naar één `theme.py` met tokens (`CANVAS_BG`, `GRIP`, `PALETTE`, …) | nieuw `theme.py` + verwijzingen (o.a. regel 1543, 1755, 1892, 1916, 1962) | geen losse hex meer in de UI-code |
| 1.4 | Licht/donker-schakelaar in menu `Beeld` | `_build_menubar` (~regel 2071) | thema wisselt live; canvas-bg volgt mee |
| 1.5 | Tooltip-helper (Toplevel op `<Enter>`/`<Leave>`) en op terse knoppen zetten (`Fit`, `x`, `<<`, `Alles`, export-knoppen) | nieuw `ui_tooltip.py` | hover toont uitleg |
| 1.6 | Dubbele mode-knoppen opruimen: houd de bovenbalk, haal ze uit het linkerpaneel | `_build_ui` (regel 1592 vs 1927) | modus-knoppen staan nog op één plek |

**Risico:** laag. Aandachtspunt: de raw `tk.Button` swatches (regel 1892) en `tk.Canvas`-achtergronden
volgen ttk-thema's niet automatisch — die moeten expliciet uit `theme.py` gevoed worden, ook bij dark mode.

---

## Batch 2 — STEP-import correctheid 🔴 ✅ GEDAAN (2.1–2.3)

**Doel:** geïmporteerde connectors komen op juiste schaal en met hun rondingen binnen.

| # | Taak | Bestand(en) | Klaar als… | Status |
|---|------|-------------|------------|--------|
| 2.1 | Unit-context lezen (`SI_UNIT` / `CONVERSION_BASED_UNIT`) en alles omrekenen naar mm | `parse_step_length_scale` + `parse_step_geometry` | inch/m-bestand komt op ware grootte binnen | ✅ |
| 2.2 | `CIRCLE`/arc-edges tesselleren naar korte segmenten i.p.v. negeren/koorde | `circle_arc_points_3d` + EDGE_CURVE-lus | ronde connectorranden zijn zichtbaar | ✅ |
| 2.3 | Unit-test met een mini-STEP in inch + een STEP met een arc | `tests/test_kabelboom_core.py` | tests groen (29/29) | ✅ |
| 2.4 | (optioneel) DXF-import als simpeler alternatief voor 2D-footprints | nieuw `dxf_import.py` + knop | DXF-outline plaatst als symbool | ⏳ open |

> Uitgevoerd: `parse_step_length_scale` herkent SI-prefixen (m/cm/mm/…) en
> `CONVERSION_BASED_UNIT` (inch/foot) en schaalt alle coördinaten + cirkelstralen
> naar mm. `circle_arc_points_3d` bemonstert cirkel-edges (volledige cirkel bij
> samenvallende eindpunten, anders de korte boog). 4 nieuwe tests.

**Restant:** 2.4 (DXF-import) blijft optioneel openstaan.

---

## Batch 3 — Architectuur: de monoliet opsplitsen 🟠 ◐ DEELS GEDAAN (3.2 + 3.3)

**Doel:** van één bestand van 10.428 regels / één god-class naar testbare modules.
Stapsgewijs; tests blijven na elke stap groen.

| # | Taak | Nieuw bestand | Status |
|---|------|---------------|--------|
| 3.1 | Dataclasses verplaatsen (`WirePath`, `ConnectorInstance`, `Leader`, `DimensionLine`, `TableBox`, `TextNote`, `ImageNote`, `StepSymbol`) + bijbehorende constants | `model.py` | ⏳ open (constants-ontvlechting nodig) |
| 3.2 | STEP-parsing + projectie | `step_import.py` | ✅ |
| 3.3 | Pure geometrie-helpers (`distance_point_segment`, `closest_point_on_segment`, …) | `geometry.py` | ✅ |
| 3.4 | Canvas- en SVG-rendering (`_draw_*`, `_svg_*`) | `rendering.py` | ⏳ open (raakt veel) |
| 3.5 | Project-IO en exports (`_project_dict`, `_load_project_dict`, netlist/BOM/PNG/PDF) | `io_project.py` | ⏳ open |
| 3.6 | UI-opbouw per paneel uit `_build_ui` halen | `ui/panels.py` | ⏳ open (laatst) |

> Uitgevoerd: `geometry.py` (pure 2D/3D-math) en `step_import.py` (STEP-parser, hangt
> alleen van `geometry` af) losgetrokken en terug-geïmporteerd in de hoofdmodule, zodat
> alle publieke namen en tests blijven werken. Tevens dode code achter `preview_project`
> opgeruimd. Hoofdbestand: 10.428 → 10.235 regels. 29/29 tests groen.

**Aanpak:** importeer terug in `kabelboom_tekenstudio.py` zodat de publieke namen (en de tests) blijven werken.
**Risico:** midden — `model.py` vergt het meeverhuizen van ~20 constants (vandaar uitgesteld);
3.4/3.5 raken veel. Klein committen, per module testen.

---

## Batch 4 — UX-polish in de panelen 🟠

**Doel:** het eigenschappenpaneel wordt leesbaar en robuust; betere oriëntatie in de app.

| # | Taak | Bestand(en) | Klaar als… |
|---|------|-------------|------------|
| 4.1 | Eigenschappen groeperen in `ttk.LabelFrame`-blokken ("Geometrie", "Elektrisch", "Stijl"); hele blokken tonen/verbergen i.p.v. rij-index | `_build_ui` (1744–1874) + `_set_visible_property_rows` (~regel 4437) | geen harde rijnummers meer; blokken klappen per context |
| 4.2 | Echte statusbalk onderaan i.p.v. de run-on hint-zin | regel 1965 | korte status + losse coördinaat-indicator |
| 4.3 | Vaste `X / Y mm`-readout rechtsonder | gebruikt `cursor_world` uit `on_motion` (regel 8285) | coördinaten altijd zichtbaar |
| 4.4 | "Sneltoetsen"-dialoog (open via `?` of Help-menu) | `_build_menubar` | overzicht i.p.v. één lange zin |
| 4.5 | Onboarding: "Nieuw uit sjabloon" / mini-startscherm | `new_project` (~regel 2677) | lege staat geeft houvast |

**Risico:** laag-midden (4.1 raakt de zichtbaarheidslogica — goed testen met elke objecttype-selectie).

---

## Batch 5 — Inhoudelijke diepte: richting ECAD 🟠 / robuustheid 🟢

**Doel:** van tekentool naar tool die connectiviteit echt begrijpt; betere diagnostiek.

| # | Taak | Bestand(en) | Klaar als… |
|---|------|-------------|------------|
| 5.1 | Pins coördinaten geven in `ConnectorInstance` (pin-posities, niet alleen `pin_count`) | `model.py` (regel 178) | elke pin heeft een wereldpositie |
| 5.2 | Netlist geometrisch afleiden: draadeinde dat een pin raakt vult `from/to` automatisch | netlist-logica (`wire_netlist_rows`, ~regel 262) + snapping | netlist klopt met de tekening |
| 5.3 | DRC uitbreiden: niet-aangesloten pins, dubbel aangesloten pins, draaddoorsnede-check | `wire_electrical_drc` (~regel 332) | nieuwe bevindingen + tests |
| 5.4 | `logging` met rotating file in appdata-map | nieuw `logging_setup.py` + aanroep in `main` (~regel 10421) | logbestand bij crashes |
| 5.5 | Brede `except Exception` vervangen door specifieke excepts waar mogelijk | hele bestand | minder verborgen bugs |

**Risico:** 5.1/5.2 zijn de grootste functionele stap — plan ze als laatste, ná de refactor (Batch 3),
zodat je ze in `model.py`/`io_project.py` netjes kunt bouwen.

---

## Aanbevolen uitvoervolgorde

1. **Batch 1** — meeste zichtbare winst, laagste risico.
2. **Batch 2** — afgebakende bugfix; los te committen.
3. **Batch 3** — fundament voor de rest; doe dit vóór Batch 5.
4. **Batch 4** — bouwt op het opgesplitste UI uit Batch 3.
5. **Batch 5** — de echte inhoudelijke sprong; profiteert van 3 en 4.

> Na elke batch: `python -m unittest discover -s tests` + handmatige rooktest, daarna commit op een eigen branch.
