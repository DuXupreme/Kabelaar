# Praktijkacceptatie voor Kabelaar 1.0

Deze lijst is een releasevoorwaarde. Automatische tests of een geslaagde build
vervangen de controle in de geïnstalleerde app niet.

## Automatische bewijsstukken

De build bewaart testresultaten, afhankelijkheidsinformatie, de gebruikte commit
en hashes onder `artifacts`. De ingebouwde ketentest:

```powershell
Kabelaar.exe --smoke-test --output C:\Dev\Kabelaar\artifacts\smoke
```

controleert selectie↔aansluitlijst, routesegmenten zonder extra materiaal,
undo/redo, heropenen, zelfstandig bestand, herstel, meegeleverde CAD-worker en
productie-export. De prestatietest gebruikt 100 connectoren en 1.000 aders in de
echte WPF-werkruimte. Het JSON-rapport bevat frame- en bewerkingstijden; de
packagebuild weigert een mislukte keten- of prestatietest.

De appmeting draait 100 pan/zoomframes. Voor bewerkingen worden eerst 10
opwarmmetingen apart bewaard en daarna 100 verplaatsingen van een geselecteerde
connector gemeten. Elke meting wacht op een daadwerkelijk nieuw getekend frame;
tussen gebaren krijgt de normale Windows-eventloop tijd voor eigenschappen.
De controlebevindingen worden na 200 ms zonder nieuwe bewerking vernieuwd.
Productie-export voert altijd een nieuwe controle uit op zijn eigen snapshot.
De ruwe tijden, referentie-pc en p95 staan in `acceptance-report.json`.
De werkruimte ververst alleen gewijzigde rapportcellen en hergebruikt ongewijzigde
connectorgeometrie. Lengtewijzigingen blijven in de rapporten zichtbaar; de
regressietests vergelijken deze met een volledige nieuwe berekening. Tijdens een
uitgestelde verversing kunnen oude knoppen in het eigenschappenpaneel geen
achterhaalde bewerking uitvoeren.

De ketentest activeert zijn venster niet en neemt geen muis- of toetsenbordinvoer
over. De werkelijke WPF-rendering blijft actief. Het rapport bewaart ook de
canvasafmetingen en schermschaling, zodat metingen vergelijkbaar zijn.

## Geautomatiseerde installatieproef

`scripts/Test-KabelaarInstallation.ps1` gebruikt uitsluitend synthetische
documenten en de echte lokaal geïnstalleerde app. De proef controleert de
Windows-snelkoppelingen en bestandsassociatie door ze daadwerkelijk te starten.
De opslagbeslissingen bij updates worden vanuit de test aangereikt; opslaan,
procesvergrendeling, downloaden uit de lokale releasemap en toepassen/herstarten
gebruiken de productiecode.

De proef onderscheidt annuleren, eerst opslaan, een ander geopend appproces en
een proces dat tijdens opslaan opent. De herstelproef wacht op de gewone
20-seconden-autosave, beëindigt uitsluitend haar eigen testproces en controleert
het herstelarchief na de upgrade. Gebruikersbestanden blijven buiten de
installatiemap en worden vóór en na de upgrade vergeleken.

De resultaten zijn technische installatie- en upgradetests op deze pc.
Ze vervangen de onderstaande visuele beoordeling en tweede-pc-proef niet.

De automatische suites omvatten modelregels, routevertakkingen, materiaaltelling,
opslagfouten/conflicten, beschadigde herstelbestanden, catalogusbehoud, echte
STEP-import in mm/inch, annuleren, contouren, schaal en alle uitvoerformaten.

Bij de lokale bedieningstest zijn connectorselectie, verslepen met gekoppelde
aders, behoud van vrije productiematen en Ctrl+Z gecontroleerd. Een synthetische
STEP-box is via de bestandsdialoog geïmporteerd; 3D-preview, 2D-projectie,
handmatig plaatsen van een pin en annuleren zijn visueel gecontroleerd.
Dit dekt niet de volledige onderstaande praktijklijst.

## Praktische controle

- [ ] Een echte productiekabelboom volledig natekenen; maatstaf lokaal openen.
- [ ] Artikel, aansluiting en lengte wijzigen; alle bladen/lijsten opnieuw produceren.
- [ ] Pinnummers, dubbele krimp, gemengde terminals, seals en plugs met brongegevens vergelijken.
- [ ] Vrije uiteinden, ongebruikte cavities en niet-meegeproduceerde tegenconnectoren controleren.
- [ ] Schaal/vrij wisselen, vaste lengte proberen te wijzigen, afwijzingspreview beoordelen.
- [ ] Hetzelfde onderdeel in meerdere aanzichten; geen extra materiaal.
- [ ] STEP-model in mm en inch; gekozen aanzicht en handmatige pinmapping vergelijken.
- [ ] PDF/SVG op ware grootte met controlemaat nameten; alle objecttypen en tabellen controleren.
- [ ] Werken met toetsenbord en schermschaling 100%, 125%, 150% en 200%.
- [ ] Nieuwe en meerdere documenten geforceerd sluiten; herstelkopieën openen.
- [ ] Mislukte opslag, externe bestandswijziging en bestaand lockbestand controleren.
- [ ] Schone installatie, echte snelkoppeling en dubbelklik op `.kabelaar`.
- [ ] Portablepakket offline starten op een tweede Windows 11 x64-pc.
- [ ] Project openen/exporteren op die pc zonder toegang tot oorspronkelijke assetmappen.
- [ ] Upgrade met bestaande bibliotheek/herstelgegevens; onopgeslagen werk behouden.

## Grenzen van versie 1

Geen elektrische schema-editor, DWG-import, import van oude Python-projecten,
automatisch routen, automatische twisttoeslag of gelijktijdige bewerking.
Schaal terug inschakelen vereist bestaande geometrie die overeenkomt met de maten.
Een pakket mag één bewerker hebben; OneDrive is geen samenwerkingsdatabase.

Een eerste lokale acceptatiebuild heet 0.9.x. Een 1.0-tag vereist deze beoordeling
en de tweede-pc-test. Publieke publicatie en een publieke updatefeed zijn aparte
besluiten.
