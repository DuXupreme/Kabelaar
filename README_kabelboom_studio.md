# Kabelboom Studio

Eenvoudig desktopprogramma om kabelbomen te maken.

## Functies
- Connectoren toevoegen/bewerken/verwijderen
- Draden (netlist) toevoegen/bewerken/verwijderen
- Visuele preview van verbindingen
- UI-schaal instelbaar in de toolbar
- Opslaan/openen als JSON project
- Export naar:
  - `*_bom.csv`
  - `*_netlist.csv`
  - `*_overview.svg`

## Starten
Gebruik Python 3 met Tkinter.

Voorbeeld:

```powershell
python .\kabelboom_studio.py
```

Of start via:

```powershell
.\start_kabelboom_studio.bat
```

`start_kabelboom_studio.ps1` zoekt automatisch naar een Python-installatie onder `%LocalAppData%\Programs\Python` en valt daarna terug op `python` of `py` in PATH.

Als PowerShell scripts lokaal geblokkeerd zijn, gebruik dan de `.bat` starter.

## Ontwikkelen / testen
Zie `README_ontwikkelen.md` voor dependency-installatie en het draaien van tests.
