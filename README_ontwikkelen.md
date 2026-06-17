# Ontwikkelen

## Installatie

Gebruik Python 3.10 of nieuwer.

```powershell
python -m pip install -r requirements.txt
```

Zorg er op Windows bij installatie voor dat `python.exe` aan PATH wordt toegevoegd. De meegeleverde startbestanden proberen eerst een lokale installatie onder `%LocalAppData%\Programs\Python` te vinden en vallen daarna terug op `python` of `py`.

`Pillow` is nodig voor afbeeldingimport en PNG/PDF-export in de tekenstudio. De basisfuncties van de apps blijven grotendeels werken zonder extra pakketten.

## Tests draaien

```powershell
python -m unittest discover -s tests
```

De huidige tests controleren vooral pure hulpfuncties, papierinstellingen en de basis van STEP parsing/projectie. Dit is bedoeld als startpunt voor verdere regressietests rond export, projectvalidatie en wire-geometrie.

## Projectbestanden

JSON-projecten worden atomisch opgeslagen via een tijdelijk bestand. Als een bestaand project wordt overschreven, blijft de vorige versie naast het project staan als `.bak`.

## Gebruikersinstellingen

Persoonlijke voorkeuren zoals UI-schaal en laatst gebruikte project-/exportmappen worden opgeslagen in `settings.json` naast de scripts. Dit bestand hoort niet bij een kabelboomproject zelf.
