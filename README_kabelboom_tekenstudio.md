# Kabelboom Tekenstudio

Interactieve tekenapp voor kabelboom-werkbladen.

## Wat je ermee kunt
- Tekenblad met technisch frame, zone-markers (nummers + letters langs de rand met tickmarks) en een volledig title block
  - Title block-velden: titel, DWG nr, revisie, formaat (auto A0–A4), blad, schaal, eenheid, tolerantieblok (X/XX/XXX), klant, getekend/gecontroleerd/goedgekeurd door + datums
  - Velden zijn in het paneel "Tekening" te bewerken en updaten live
- Herbruikbare blokken: sla een selectie op als benoemd blok en voeg het later opnieuw in
  - Blokken bewaren connectors (incl. STEP-symbool), draden, leaders, maatlijnen, tabellen en tekst
  - Nieuwe ID's en interne referenties worden automatisch hernummerd bij invoegen
  - Beheer via het paneel "Bibliotheek", het menu `Tools`, of rechtermuisknop op een selectie
- STEP connectorbestand inladen met **3D wireframe preview**
- In de preview kun je het model roteren en daarna projectiezijde kiezen:
  - `Top (XY)`, `Bottom (XY)`, `Front (XZ)`, `Back (XZ)`, `Left (YZ)`, `Right (YZ)`
- Connectoren plaatsen en verplaatsen op het blad
- Connectoren krijgen part number, pin-count en optionele pinlabels/cavity labels
- Draden tekenen: elke klik zet direct een endpoint en maakt een segment
- Elk draadsegment is apart selecteerbaar
- Gebogen draden en twisted pairs (instelbaar via contextmenu en eigenschappenpaneel)
- Maatlijnen (dimensions) tekenen met 2 klikken; lengte wordt automatisch gemeten
  - Richting horizontaal / verticaal / uitgelijnd (auto-keuze op basis van de twee meetpunten)
  - Tolerantie (bv. `±10`) en handmatige maatwaarde-override per maatlijn
  - Pijlpunten, extension lines en instelbare offset, kleur, lijndikte en tekstgrootte
  - Snap naar connectorcentra en draad-/leader-eindpunten
- Volledige undo/redo voor modelwijzigingen (incl. tekenen, transformeren, tabellen, eigenschappen)
- Snap-instellingen:
  - Grid snap met instelbare stap in mm
  - Endpoint snap naar connectorcentra, draad- en leader-eindpunten en tabelhoeken
- Leaders toevoegen (2 klikken + tekst)
- Tabellen plaatsen (2 hoeken + rij/kolom)
- Vast eigenschappenpaneel (links) voor geselecteerd object:
  - Wire: kleur, dikte, label
  - Leader: kleur, dikte, tekst, tekstgrootte, pijlgrootte en optioneel tekstkader
  - Connector: lijnkleur, lijndikte, schaal
  - Tabel: randkleur, lijndikte
- Workflowbalk boven het tekenblad voor snel wisselen tussen selecteren, draad, leader, connector en tabel
- Linkerpaneel is geordend als Start, Eigenschappen, Bibliotheek, Tekening en Bestand/export
- Kleur kiezen via kleurpicker + snelkleur-palet in het vaste eigenschappenpaneel
- Contextmenu (rechtermuisknop) met relevante acties:
  - Dupliceren, verwijderen, volgorde (voorgrond/achtergrond)
  - Transformeren (roteren, spiegelen) voor ondersteunde objecten
  - Tabelacties: rijen/kolommen invoegen/verwijderen + tekstuitlijning
- Opslaan/openen als JSON
- Projectinstellingen/default stijlen en view/snap-voorkeuren worden mee opgeslagen
- Exporteren naar SVG
- Exporteren naar netlist CSV en BOM CSV vanuit draadmetadata
- Menubalk met engineering-acties (`Bestand`, `Bewerken`, `Beeld`, `Tools`)
- Projectcontrole (DRC) + projectinventarisrapport
- DRC controleert draadpinnen tegen connector pin-count en bekende pinlabels

## Sneltoetsen / bediening
- `Muiswiel`: zoom
- `Middenmuis + slepen`: pan
- `Beeld > UI schaal`: interface groter/kleiner zetten
- `SHIFT` tijdens draad tekenen: volgende segment wordt exact horizontaal of verticaal
- `Ctrl+Z` / `Ctrl+Y`: undo / redo
- `Ctrl+S`: opslaan
- `Ctrl+Shift+S`: opslaan als
- `Ctrl+O`: open project
- `Ctrl+N`: nieuw project
- `Ctrl+D`: dupliceer selectie
- `Delete`: verwijder selectie
- `ENTER`: actieve draadketen stoppen
- `ESC`: altijd terug naar `Selecteer / verplaats`
- `Rechtermuisklik`: contextmenu met relevante acties voor huidige mode/selectie
- In contextmenu's zijn `Undo/Redo` direct beschikbaar
- Voor draadsegmenten: contextmenu bevat ook `Draadvorm` (recht/gebogen/twisted) en bochtaanpassingen
- In `Selecteer / verplaats`: sleep interne tabelranden om kolombreedtes/rijhoogtes te resizen
- `Dubbelklik` op tabelcel: celtekst bewerken
- `Dubbelklik` op object (in Select-mode): object selecteren en direct in vast eigenschappenpaneel aanpassen

## Starten
```powershell
python .\kabelboom_tekenstudio.py
```

Of via:
- `start_kabelboom_tekenstudio.bat`
- `start_kabelboom_tekenstudio.ps1`

`start_kabelboom_tekenstudio.ps1` zoekt automatisch naar een Python-installatie onder `%LocalAppData%\Programs\Python` en valt daarna terug op `python` of `py` in PATH.

Als PowerShell scripts lokaal geblokkeerd zijn, gebruik dan de `.bat` starter.

## Ontwikkelen / testen
Zie `README_ontwikkelen.md` voor dependency-installatie en het draaien van tests.
