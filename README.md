# Kabelaar

Windows-desktopapp voor productietekeningen van kabelbomen. C# / .NET 10, WPF,
SkiaSharp en een apart C++/Open CASCADE-proces voor STEP.

De huidige versie is **0.9.0, lokale acceptatieversie**. De aanduiding 1.0 is
gereserveerd voor de volledige praktijktest, inclusief installatie op een tweede
pc en beoordeling van een echte productietekening. Er is geen publieke updatefeed.

## Werken met de app

1. Open een project of kies **Bestand → Synthetisch voorbeeld openen**.
2. Definieer onderdelen onder **Onderdelen**, of importeer lokaal een STEP-model.
   Kies een projectie en leg de pinnummers en pinposities expliciet vast.
3. Selecteer een connector in de bibliotheek en plaats hem op het blad.
4. Kies **Ader**; klik een beginpin, tussenpunten en de eindpin. Enter of rechtsklik
   rondt een vrij uiteinde af.
5. Selecteer de ader op het blad of in de aansluitlijst. Vul artikel, kleur,
   doorsnede, aansluitingen en lengtetoeslagen in bij **Eigenschappen**.
6. Vul per connectorcavity de gemonteerde terminal, seal of plug in. Leg expliciet
   vast welke cavities ongebruikt zijn.
7. Plaats maatlijnen en gekoppelde tabellen. Gebruik extra bladen en detailaanzichten
   voor productietabellen, montagegegevens en connectorinformatie.
8. Controleer met F7. Conceptuitvoer krijgt een watermerk; productie-uitvoer vereist
   een geslaagde controle. Exporteer PDF, SVG, PNG en CSV vanuit één projectsnapshot.

De projectboom geeft ook vergrendelde annotaties en onderleggers toegang tot hun
eigenschappen. PDF-pagina's worden als referentieafbeelding weergegeven. De
oorspronkelijke PDF en de gebruikte afbeelding worden in het project opgenomen.

Een bibliotheekbewerking maakt een nieuwe onderdeelrevisie. Bestaande toepassingen
worden alleen na het verschillenoverzicht en een expliciete keuze bijgewerkt.

## Geometrie en productiegegevens

- **Op schaal:** routegeometrie in millimeters bepaalt de fysieke routelengte.
- **Vrije opmaak:** de vormgeving wijzigt geen fysieke maten.
- **Lengte vast:** een wijziging die de fysieke lengte verandert wordt afgewezen.
- **Afdrukschaal:** schaalt de bladweergave, onafhankelijk van de fysieke gegevens.

Een ader is één fysiek object met een geordende route van één of meer takken.
Extra tekenpunten en detailaanzichten creëren geen materiaal. Kniplengtes volgen
de route plus expliciete toeslagen, of een herkenbare handmatige waarde.
Twistweergave berekent geen extra draadlengte.
Bij een nieuw getekende route in vrije opmaak moet je de fysieke taklengte zelf
invullen; de app neemt die nooit stilzwijgend over uit de tekening.

Een wijziging van een fysieke lengte in een geschaald aanzicht kan andere
geschaalde aanzichten inconsistent maken. De controle wijst die aan voor correctie;
de app verplaatst andere aanzichten niet automatisch.

PDF-uitvoer bewaart de geometrieschaal. De PDF-backend rondt de papierrand af op
hele PDF-punten: A3 wordt circa 420,158 × 297,039 mm. Druk af op 100% zonder
automatisch passend maken en controleer de maatlijn vóór gebruik op ware grootte.

## Bediening

| Handeling | Bediening |
|---|---|
| Pan / zoom | Middenmuis of spatie+slepen / muiswiel |
| Meervoudige selectie | Ctrl+klik of selectiekader |
| Routepunt wijzigen | Selecteer ader, sleep een vierkant greeppunt |
| Routepunt toevoegen | Dubbelklik op de route |
| Selecteren / tekenen | Esc / W |
| Roteren / dupliceren | R / Ctrl+D |
| Ongedaan / opnieuw | Ctrl+Z / Ctrl+Y |
| Opslaan / opslaan als | Ctrl+S / Ctrl+Shift+S |
| Blad passend / controleren | Home / F7 |

## Bestanden en herstel

`.kabelaar` is een ZIP-pakket met versieerbare JSON en ingesloten assets.
Bronbestanden of de lokale bibliotheek zijn op de ontvangende pc niet nodig.
Onderdeelbibliotheken gebruiken `.kabelaarlib`, sjablonen `.kabelaartemplate`.

Opslaan gebruikt een tijdelijk bestand en een atomische vervanging met herstelbare
vorige versie. Een lokale lock plus inhoudsvingerafdruk signaleren bekende
overschrijfconflicten. **Eén bewerker tegelijk** blijft de afspraak; OneDrive biedt
geen betrouwbare lock tussen pc's.

Autosave bewaart gewijzigd werk iedere 20 seconden, ook vóór de eerste bestandsnaam.
Instellingen, bibliotheek, log en herstelbestanden staan onder
`%LOCALAPPDATA%\Kabelaar.UserData`, buiten de Velopack-installatiemap.

## Ontwikkeling

```powershell
dotnet restore Kabelaar.slnx
dotnet build Kabelaar.slnx -m:1
dotnet test Kabelaar.slnx --no-build -m:1
```

De native worker moet ook beschikbaar zijn om de volledige desktopapp te bouwen:
zie [native/Kabelaar.CadWorker/README.md](native/Kabelaar.CadWorker/README.md) en
de scripts in die map. De worker-runtime wordt vanuit `artifacts/native-worker`
in de app opgenomen. Voor gebruikers zijn geen Python-, .NET- of CAD-installaties nodig.

`scripts/Build-Kabelaar.ps1` bouwt, test en publiceert een Windows x64-versie.
`scripts/Package-Kabelaar.ps1` maakt lokale Velopack-installatie- en portablepakketten
uit een schone, vastgelegde en geteste commit. De scripts en hun parameters zijn de
bron voor de exacte bouwprocedure.

## Indeling

| Module | Verantwoordelijkheid |
|---|---|
| `Kabelaar.Core` | Fysieke gegevens, documentmodel, bewerkingen, undo/redo, controle en materiaallijsten |
| `Kabelaar.Rendering` | Renderer-onafhankelijke tekenobjecten, ruimtelijke index, Skia, vector- en tabeluitvoer |
| `Kabelaar.Infrastructure` | Projectpakketten, herstel, bibliotheken, locks en STEP-procescommunicatie |
| `Kabelaar.App` | Windows-werkruimte, editor, formulieren en installatie-integratie |
| `native/Kabelaar.CadWorker` | Geïsoleerde STEP-parser, mesh en zes contourprojecties |
| `tests` | Synthetische regressie- en integratietests |

De Python-bronboom is vervangen. De laatste Python-toestand blijft in Git-commit
`77470c6`; er wordt geen tweede productlijn onderhouden.

Bedrijfsbestanden horen niet in deze repository of openbare testdata. De app
gebruikt geen account en verstuurt geen projectgegevens.
