# Kabelaar

Windows-desktopapp voor productietekeningen van kabelbomen. C# / .NET 10, WPF,
SkiaSharp en een apart C++/Open CASCADE-proces voor STEP.

De huidige bronversie is **0.9.4, lokale acceptatieversie**. De aanduiding 1.0 is
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

## Tekenstijl en leesbaarheid

Open **Beeld → Tekenstijl…**, of gebruik **Tekenstijl…** bij de projecteigenschappen
wanneer niets is geselecteerd. De instellingen gelden uitsluitend voor het actieve
aanzicht; een detailaanzicht kan een andere opmaak behouden.

- **Productieoverzicht** gebruikt dunne contouren, duidelijkere voedingsdraden en
  korte twistmarkeringen. Connectornamen blijven zichtbaar; automatische
  aderlabels, twistlabels, artikelnummers, pinnummers en pindots worden verborgen.
- **Aansluitdetail** toont de labels en pininformatie. Lijndiktes, teksthoogte,
  twistuitwijking en getekende spoed zijn afzonderlijk instelbaar.
- Selecteer een connector om de naam, artikel- en pininformatie, teksthoogte en
  labelpositie per plaatsing te wijzigen. In **vrije opmaak** kun je ook de
  connectorvergroting aanpassen. Splices hebben eigen labelinstellingen.
- Selecteer een aansluit- of pinlijst en kies bij **Aansluitingen van** één
  connector of alle connectoren. De tabel blijft gekoppeld aan de fysieke gegevens.

Een voorinstelling verandert de tekening pas na **Toepassen**. Eén keer
**Ongedaan maken** herstelt de volledige wijziging. Weergavewijzigingen behouden
aansluitidentiteiten, materiaalaantallen en productielengtes; getekende twistspoed
staat los van de fysieke spoed. Ook verborgen pindots blijven beschikbaar voor
snapping. In een aanzicht **op schaal** blijft de connectorvergroting 1.

## Geometrie en productiegegevens

Selecteer een ader en gebruik **Draad tekenen** bovenaan de eigenschappen.
Kies recht of gebogen, een hele routetak of één segment, bochtuitwijking en een
eigen lijndikte. Ook een draad met slechts twee eindpunten kan een bocht krijgen.
De gele bolletjes sturen de richting en kromming; de blauwe punten verplaatsen
knopen. Handvatten kunnen onafhankelijk, uitgelijnd of gespiegeld werken.
**Bocht omkeren** en **Handvatten herstellen** zijn expliciete acties.

In vrije opmaak geldt de vorm standaard alleen voor de geselecteerde ader.
**Bewerk hele bundel** vervangt de afzonderlijke vormen door een gedeelde route.
Bij slepen kies je een segment, tak of hele ader en of aangesloten uiteinden
blijven zitten, het aangesloten knooppunt meenemen of expliciet loskomen.
De werkelijke geometrie wordt tijdelijk getoond. Escape annuleert; bevestigen
vormt één undo-stap. Een afgewezen lengtewijziging verandert het project niet.

Bij meervoudige selectie worden uitsluitend gewijzigde gemeenschappelijke
eigenschappen toegepast; verschillende waarden worden als zodanig getoond.
De kleurkiezer en het palet wijzigen de tekenkleur. Fysieke kleurcode en
aderdoorsnede blijven afzonderlijke productiegegevens.

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

Vanaf 0.9.4 zijn de volgende bewerkingen beschikbaar:

- **Draadlabels:** selecteer een ader en kies automatisch, eigen tekst of verborgen.
  Een expliciet lege tekst verbergt het label. Versleep het labelhandvat zonder
  draadnummer, signaal of productiematen te wijzigen. Oversteekboogjes zijn per
  ader instelbaar; tekenvolgorde bepaalt de bovenliggende ader.
- **Bewerken → Dupliceren / Parallelle aders:** bekijk eerst de nieuwe aders,
  onderdelen en aansluitingen. Externe aansluitingen van een kopie worden open
  uiteinden; interne koppelingen worden naar de nieuwe onderdelen overgezet.
  Onvolledige kabel- en twistgroepen worden expliciet in het voorstel vermeld.
- **Selectieblokken:** sla een selectie op als `.kabelaarblock`. Het bestand bevat
  de benodigde onderdelen en assets. Invoegen maakt zelfstandige bewerkbare
  objecten. Een extra detailaanzicht blijft een afzonderlijke actie zonder extra materiaal.
- **Contextmenu en transformeren:** rechtsklik voor acties op de selectie.
  Roteren, spiegelen en voor-/achtergrond gebruiken dezelfde commando's als het menu.
  Gekoppelde uiteinden en lengtevergrendelingen worden vooraf gecontroleerd.
- **Snapping:** de schakelaar Eindpunten werkt onafhankelijk van het raster.
  Ook vrije draaduiteinden, leaderpunten en tabelhoeken zijn snapdoelen.
  Snapping op zich maakt geen elektrische verbinding.
- **Leaders en tabellen:** versleep afzonderlijke leaderpunten en connectorlabels.
  Vrije tabellen hebben bewerkbare cellen, rijen en kolommen; gekoppelde tabellen
  behouden hun productiegegevens. Wijzig uitlijning, breedtes, rijhoogte en marges
  via Eigenschappen; versleep de buitenhoek of een kolomgrens op het blad.
- **Plakken:** Ctrl+V op het blad plaatst tekst of een ingesloten afbeelding.
  In tekstvelden blijft Ctrl+V normaal werken. Plakken als onderlegger maakt een
  afzonderlijke, niet-afdrukbare referentie. Het klembord wordt alleen gelezen bij plakken.
- **Titelblok en zones:** Beeld → Titelblok en bladzones schakelt de uitgebreide
  variant expliciet in. Oude bladen behouden hun bestaande kader.
- **Aanvullende gegevens:** pinfunctie, netnaam, afscherming, aansluitnotities en
  tegenconnectorreferenties zijn afzonderlijk bewerkbaar. Ze veroorzaken geen
  extra fysieke verbindingen of materiaal in de stuklijst.
- **STEP-preview:** sleep voor vrij draaien, midden-/rechtermuis voor verschuiven,
  muiswiel voor zoomen en Passend voor het gehele model. De previewcamera wijzigt
  de gekozen productieprojectie niet.
- **Bibliotheek:** Onderdelen → Verwijderen/Herstellen verwijdert uitsluitend uit
  de eigen catalogus. Bestaande projecten houden hun ingesloten definities.
- **Werkruimte:** Beeld → Werkruimte bewaart paneelzichtbaarheid, licht/donker,
  interfaceschaal en standaarden voor nieuwe objecten buiten de installatie.
- **Verbindingsvoorstellen:** kies een voorstel bij een vrij uiteinde; een einde
  op een ader kan een nieuwe splice voorstellen. Bekijk de fysieke gevolgen vóór
  toepassen. Splitsen markeert onbekende kniplengtes en aansluittoeslagen ter controle;
  een oude volledige kniplengte wordt niet naar beide nieuwe aders gekopieerd.
  Kabel-/twistgroepen en gekoppeld takmateriaal moeten eerst expliciet worden verdeeld.
- **Massa en algemene knopen:** kies een grafisch symbool of een aansluitbaar
  referentiepunt. Een fysieke kabelboomsplitsing blijft een splice. Ringterminals
  en andere gemonteerde onderdelen worden afzonderlijk vastgelegd en geteld.

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

Vanaf 0.9.4 gebruiken opgeslagen projecten **schema 4** voor labels, vrije
tabellen, tekenvolgorde, referentiepunten en aanvullende gegevens. De app opent
schema 1, 2 en 3 via opeenvolgende migraties met behoud van bestaande vormen,
aansluitingen en productiegegevens. Openen wijzigt het bronbestand niet; na
opslaan gebruikt het pakket schema 4. Kabelaar 0.9.3 en ouder weigeren dit
nieuwere formaat. Maak een kopie als het oorspronkelijke project ook met een
oude appversie beschikbaar moet blijven. Onbekende nieuwere of beschadigde
formaten worden geweigerd; herstelbestanden en bibliotheken gebruiken dezelfde controles.

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
