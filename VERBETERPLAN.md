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

## Batch 3 — Architectuur: de monoliet opsplitsen 🟠 ✅ GEDAAN

**Doel:** van één bestand van 10.428 regels / één god-class naar testbare modules.
Stapsgewijs; tests blijven na elke stap groen.

| # | Taak | Nieuw bestand | Status |
|---|------|---------------|--------|
| 3.1 | Dataclasses verplaatsen (`WirePath`, `ConnectorInstance`, `Leader`, `DimensionLine`, `TableBox`, `TextNote`, `ImageNote`, `StepSymbol`) + bijbehorende constants + model-helpers | `model.py` | ✅ |
| 3.2 | STEP-parsing + projectie | `step_import.py` | ✅ |
| 3.3 | Pure geometrie-helpers (`distance_point_segment`, `closest_point_on_segment`, …) | `geometry.py` | ✅ |
| 3.4 | Canvas- en SVG-rendering (`_draw_*`, `_svg_*`) | `rendering.py` (`RenderingMixin`) | ✅ |
| 3.5 | Project-IO en data-export (`_project_dict`, `_load_project_dict`, save/open/new, SVG/netlist/BOM) | `io_project.py` (`ProjectIOMixin`) | ✅ |
| 3.6 | UI-opbouw uit `_build_ui` halen | `ui_panels.py` (`UIBuilderMixin`) | ✅ |

> Uitgevoerd: `geometry.py`, `step_import.py`, `model.py`, `rendering.py`
> (`RenderingMixin`), `io_project.py` (`ProjectIOMixin`) en `ui_panels.py`
> (`UIBuilderMixin`: `_build_ui`, panelen, thema/UI-schaal, menubar) losgetrokken.
> Mixin-aanpak houdt gedrag identiek (methodes verhuizen letterlijk mét `self`).
> `PROJECT_SCHEMA_VERSION` + `DEFAULT_WIRE_BRIDGE_*` → `model.py`,
> `LEFT_PANEL_DEFINITIONS` + `UI_NAMED_FONTS` → `ui_panels.py` (circulaire imports
> voorkomen). Dode code achter `preview_project` opgeruimd.
> **Hoofdbestand: 10.428 → 7.677 regels (−26%).** 29/29 tests groen +
> render/SVG/IO/UI-smoketests.

`HarnessDrawingStudio(UIBuilderMixin, RenderingMixin, ProjectIOMixin, tk.Tk)` —
de god-class is nu een dunne controller met de logica verdeeld over zes modules.

> **Bewust in de hoofdklasse gebleven:** PNG/PDF-export (`_render_page_image`,
> `export_png/pdf`) — verweven met teken-helpers (`_title_block_drawing`, `_pil_font`).
> Kan later naar een gedeelde rendering/io-laag.

> **PNG/PDF-export** (`_render_page_image`, `export_png/pdf`) is bewust in de hoofdklasse
> gebleven: die is verweven met teken-helpers (`_title_block_drawing`, `_pil_font`, …)
> en past beter bij een latere rendering-/io-samenvoeging dan bij deze stap.

---

## Batch 4 — UX-polish in de panelen 🟠 ✅ GEDAAN

**Doel:** het eigenschappenpaneel wordt leesbaar en robuust; betere oriëntatie in de app.

> Uitgevoerd: eigenschappen gegroepeerd in `ttk.LabelFrame`-blokken (Stijl, Tekst,
> Geometrie, Draad, Elektrisch, Connector, Maatlijn) die per selectie automatisch
> in-/uitklappen; intern stabiele veld-id's i.p.v. harde grid-rijen. Echte statusbalk
> onderaan met live status + vaste X/Y mm-readout. Sneltoetsen-dialoog (Help-menu en F1).
> Onboarding: lege-staat-overlay op het canvas + "Nieuw uit sjabloon" (bladformaatkeuze).

| # | Taak | Bestand(en) | Klaar als… |
|---|------|-------------|------------|
| 4.1 | Eigenschappen groeperen in `ttk.LabelFrame`-blokken ("Geometrie", "Elektrisch", "Stijl"); hele blokken tonen/verbergen i.p.v. rij-index | `_build_ui` (1744–1874) + `_set_visible_property_rows` (~regel 4437) | geen harde rijnummers meer; blokken klappen per context |
| 4.2 | Echte statusbalk onderaan i.p.v. de run-on hint-zin | regel 1965 | korte status + losse coördinaat-indicator |
| 4.3 | Vaste `X / Y mm`-readout rechtsonder | gebruikt `cursor_world` uit `on_motion` (regel 8285) | coördinaten altijd zichtbaar |
| 4.4 | "Sneltoetsen"-dialoog (open via `?` of Help-menu) | `_build_menubar` | overzicht i.p.v. één lange zin |
| 4.5 | Onboarding: "Nieuw uit sjabloon" / mini-startscherm | `new_project` (~regel 2677) | lege staat geeft houvast |

**Risico:** laag-midden (4.1 raakt de zichtbaarheidslogica — goed testen met elke objecttype-selectie).

---

## Batch 5 — Inhoudelijke diepte: richting ECAD 🟠 / robuustheid 🟢 ✅ GEDAAN (5.5 deels)

**Doel:** van tekentool naar tool die connectiviteit echt begrijpt; betere diagnostiek.

> Uitgevoerd: 5.1 pin-posities (`ConnectorInstance.pin_offsets_mm` + auto-layout,
> elke pin heeft een wereldpositie, gerenderd bij selectie). 5.2 netlist geometrisch
> afleiden (draadeinden koppelen aan dichtstbijzijnde pin; Tools-menu). 5.3 DRC uitgebreid
> met niet-aangesloten pins + niet-standaard doorsnede-check (+ tests). 5.4 logging met
> roterend bestand in app-data + excepthook (`logging_setup.py`). 5.5 deels: overgeslagen
> laad-items worden nu gelogd en enkele parse-fallbacks versmald; brede catches rond
> optionele imports/Tk-teardown bewust gelaten.

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

## Batch 6 — Professionalisering: domein-diepte + soepelheid 🔴 ⏳ BEZIG

**Doel:** van "goede tekentool" naar "tool die engineers verkiezen boven AutoCAD Electrical".
Twee sporen die elkaar versterken: **6A** maakt het resultaat een echte deliverable
(connectiviteit → tabellen → strakke PDF), **6B** zorgt dat het soepel blijft op een vol blad
— gemeten, niet gegokt.

### 6A — Harness-domein & professionele output 🔴

| # | Taak | Bestand(en) | Klaar als… | Status |
|---|------|-------------|------------|--------|
| 6A.1 | Automatische wire numbering (sequentieel, instelbaar prefix/format) + "Hernummer draden"-actie | `model.py`, main, `io_project.py` | nieuwe draden krijgen auto een nummer; bestaande in één actie te hernummeren | ⏳ |
| 6A.2 | From/To-draadtabel als plaatsbaar object op het blad, gegenereerd uit de netlist | `rendering.py`, `model.py` | tabel toont Wire/Van/Naar/mm²/lengte en ververst bij wijziging | ⏳ |
| 6A.3 | Connector-pinout/cavity-tabel uit `ConnectorInstance.pin_offsets_mm` + labels | `rendering.py`, `model.py` | per connector een pinlijst-tabel op het blad | ⏳ |
| 6A.4 | Totale draadlengte per doorsnede/kleur zichtbaar ín de app (niet alleen CSV) | `model.py` (`wire_bom_rows` bestaat al) | lengte-staat in een paneel/dialoog | ⏳ |
| 6A.5 | PDF/print-polish: genormde marges, scherpe lijnen, embedded fonts | `_render_page_image`, `export_pdf` | export oogt als engineering-deliverable | ⏳ |

### 6B — Gemeten performance 🟠

| # | Taak | Bestand(en) | Klaar als… | Status |
|---|------|-------------|------------|--------|
| 6B.1 | Prestatiemeter (redraw-ms in statusbalk, `Beeld ▸ Prestatiemeter`) + stress-generator (`Tools`) + headless benchmark | `rendering.py`, main, `tools/bench_redraw.py` | redraw-tijd zichtbaar én reproduceerbaar meetbaar | ✅ |
| 6B.2 | ~~Statische bladlaag cachen (canvas-items hergebruiken)~~ — **geprobeerd, teruggedraaid** | `rendering.py` | géén winst: Tk rastert per frame de hele dirty-regio opnieuw, ook hergebruikte items | ❌ |
| 6B.3 | **Wire-bridges broad-phase**: draadparen met niet-overlappende bounding box meteen overslaan → O(n²)→~O(n) | main (`_wire_bridge_specs`) | ~3× sneller bij 300 draden (744→247 ms) | ✅ |
| 6B.4 | **Incrementeel slepen**: tijdens object-drag de achtergrond bevriezen en alleen het versleepte object in een 'dragmove'-laag verversen; volledige redraw alleen bij loslaten (reconcile) | `rendering.py` (filter + guards) + main (drag-orchestratie) | **4,6 ms/frame i.p.v. 220 ms — ~48× sneller, slepen ~60 fps+ ongeacht itemaantal** | ✅ |

> **Meetresultaat (6B.1 — A3, fit-to-view; let op: hoge run-variantie op Windows+Tk):**
> - **Leeg blad ≈ 70–160 ms** voor 759 items (frame/zone-markers/title block). Niet de draden maar het
>   **aantal canvas-items dat Tk per frame moet rasteren** is de grootste vaste kost.
> - **6B.2-les:** canvas-items hergebruiken i.p.v. `delete("all")` gaf **0 winst** — Tk rastert elke
>   frame de hele dirty-regio opnieuw, of items nu hergebruikt worden of niet. Daarom teruggedraaid. Een
>   statische laag helpt pas als hij naar één bitmap geplat wordt (goedkope blit) — apart, groter werk.
> - **6B.3-winst:** wire-bridges waren O(n²) in draden; broad-phase maakt 300 draden ~3× sneller (744→247 ms).
>   Bij extreem dichte, willekeurig kruisende draden (1000+) verschuift de kost naar het *rasteren* van de
>   boog-items zelf — niet realistisch voor een gebundelde kabelboom.
> - **6B.4-winst (de grote):** incrementeel slepen — achtergrond bevriezen, alleen het versleepte object
>   per frame verversen — bracht een sleepframe van **220 ms naar 4,6 ms (~48×, ~217 fps)**. Slepen is nu
>   soepel ongeacht het aantal objecten; dit omzeilt de rasterisatie-kost volledig. Reshape-drags
>   (draadeinde/tangent/bocht) gebruiken nog de volledige redraw — kandidaat voor dezelfde aanpak.
> Reproduceer met `python tools/bench_redraw.py`.

**Risico:** 6A.2/6A.3 raken `model.py` + IO → schema-versie ophogen en migratie meenemen.
6B.4 raakt de drag-handlers + kern-redraw (visuele/gedragsregressie mogelijk) → per objecttype testen.

**Volgorde:** 6B.1 ✅ → 6B.3 ✅ → 6B.4 ✅ → **6A.1** (wire numbering, hoog dagelijks nut)
→ 6A.2/6A.3 (deliverables) → 6A.5 (PDF-polish). Resteert in 6B: reshape-drags incrementeel maken (optioneel).

---

## Batch 7 — Renderkwaliteit + echte STEP-import 🔴 ⏳ BEZIG

**Aanleiding (gebruikersfeedback):** de twee grootste pijnpunten in dagelijks gebruik —
de "trapjes" op de lijnen, en STEP-bestanden die slecht importeren.

### 7A — Anti-aliased, image-based rendering 🔴

Probleem: `tk.Canvas` doet geen anti-aliasing → trapjes op diagonalen, ruwe boogjes. POC
(`tools/aa_render_poc.py`) bewijst: supersampling (2–3× + LANCZOS) geeft strakke lijnen; een
vol A3-blad met 300 lijnen @ 2× ≈ **78 ms** per regen (gecachet → alleen bij wijziging).

Architectuur: render de scène (kader/zones/title block/wires/connectors/tabellen/tekst) met
Pillow op 2× mét AA → één `create_image`. Cache de scene-image (signature zoals 6B.2, nu wél
zinvol want het is één blit). Interactie blijft Tk-overlay: selectie-handles, het versleepte
object (sluit aan op 6B.4), temp-geometrie, snap-markers. Bij continu zoomen op canvas-scale
tonen en pas "scherp" renderen na stilstand (~120 ms), om zoom-lag te voorkomen.

| # | Taak | Bestand | Klaar als… | Status |
|---|------|---------|------------|--------|
| 7A.1 | POC: AA-techniek + rendertijd bewijzen | `tools/aa_render_poc.py` | kwaliteit + perf bevestigd | ✅ |
| 7A.2 | `aa_render.py`: scène → AA PIL-image voor gegeven view + modeldata (hergebruik `_render_page_image`) | nieuw | losse, testbare renderfunctie | ⏳ |
| 7A.3 | Image-laag in `redraw()` i.p.v. wire/connector-Tk-items; cache + invalidatie | `rendering.py` | wires/connectors crisp op scherm | ⏳ |
| 7A.4 | Interactie-overlays (handles/drag/temp) bovenop de image-laag | `rendering.py` | selectie/slepen werkt als voorheen | ⏳ |
| 7A.5 | Zoom-settle (scherp na stilstand) + export op dezelfde AA-renderer | main + export | continu zoomen soepel; scherm == deliverable | ⏳ |

### 7B — Echte STEP-import via kernel 🟠

Probleem: de regex-parser mist B-splines, niet-cirkel-edges en surfaces → veel bestanden
importeren slecht (RingTerminals = 11 segmenten + misleidende noodgreep-diagonaal).

Aanpak: OpenCASCADE-kernel (`OCP`/`cadquery-ocp` of `cascadio`, wheels op PyPI). Laden →
tessellatie → projecteren naar gekozen vlak → 2D-outline. Graceful fallback naar de huidige
regex-parser als de kernel ontbreekt (dev/basisfuncties blijven werken).

| # | Taak | Bestand | Klaar als… | Status |
|---|------|---------|------------|--------|
| 7B.1 | Kernel kiezen + pip-install valideren (OCP vs cascadio) | `requirements.txt` | importeert in dev | ⏳ |
| 7B.2 | `step_kernel.py`: STEP → mesh/edges → projectie, achter dezelfde interface als nu | nieuw | RingTerminals komt compleet binnen | ⏳ |
| 7B.3 | Fallback naar regex-parser als kernel ontbreekt | `step_import.py` | dev zonder kernel blijft werken | ⏳ |
| 7B.4 | PyInstaller `.spec` + Velopack-build met OCC-binaries (bundle groeit fors) | `.spec`, build | installer draait met kernel | ⏳ |

**Risico:** 7A is een render-laag-herinrichting (visuele regressie → PNG-vergelijking vóór/ná).
7B vergroot de build/bundle aanzienlijk en de échte test (op connector-STEP's) gebeurt buiten
dit dev-env, door de maker.

**Volgorde:** 7A.1 ✅ → 7A.2/7A.3 (de zichtbare winst) → 7A.4/7A.5 → 7B parallel zodra de
kernelkeuze gevalideerd is.

---

## Aanbevolen uitvoervolgorde

1. **Batch 1** — meeste zichtbare winst, laagste risico.
2. **Batch 2** — afgebakende bugfix; los te committen.
3. **Batch 3** — fundament voor de rest; doe dit vóór Batch 5.
4. **Batch 4** — bouwt op het opgesplitste UI uit Batch 3.
5. **Batch 5** — de echte inhoudelijke sprong; profiteert van 3 en 4.

> Na elke batch: `python -m unittest discover -s tests` + handmatige rooktest, daarna commit op een eigen branch.
