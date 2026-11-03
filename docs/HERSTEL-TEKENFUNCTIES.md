# Herstel tekenfuncties

Uitgangspunt: commit `4b1caa6`, lokaal geïnstalleerde versie 0.9.2.
Dit document houdt implementatie en releasebewijs uit elkaar. Een aangevinkt
implementatiepunt is geen verklaring van praktische gebruikersacceptatie.

## Eerste lokale update: 0.9.3

- [x] 1. Bézier-handvatten, bochtuitwijking, omkeren en herstellen.
- [x] 2. Recht/gebogen direct kiezen, ook met twee eindpunten.
- [x] 3. Visuele lijndikte per ader per aanzicht.
- [x] 4. Kleurkiezer en palet.
- [x] 5. Segment/tak/ader verplaatsen; aansluitingen behouden/meenemen/losmaken.
- [x] 6. Tijdelijke geometrievoorvertoning, annuleren en één undo-stap.
- [x] 7. Expliciete eigenschappenpatch voor meervoudige selectie.
- [x] Maatlijn gebruikt de ingestelde lijndikte.
- [x] Tabel gebruikt ingestelde lijnkleur en lijndikte.
- [x] Schema 3, migratie, regressies en exports getest.
- [x] Volledige verborgen WPF-ketentest en prestatiedrempels geslaagd.
- [x] Geteste commit verpakt, upgrade en portable gecontroleerd, geïnstalleerd.

Afgerond op 20 oktober 2026 op commit
`27f2e1d280c4069bf0ff9eccf6184feaedd69395`: 199 automatische tests en 70
verborgen appcontroles geslaagd. De definitieve prestatierun haalde p95 9,9 ms
voor pan/zoom, 74,2 ms voor gewone bewerkingen en 13,6 ms voor bochtvoorvertoning.
De eerdere prestatieblokkade is opgelost met gerichte route- en tekenoptimalisaties.
Ook gelijktijdig losmaken van meerdere aders bepaalt cavity-bezetting nu vanuit
de volledige eindtoestand, met regressiecontrole op undo/redo.

Opgeslagen lokaal releasebewijs:
- `artifacts/installed-restoration-093/result.json`: upgrade 0.9.2 → 0.9.3,
  herstel, snelkoppelingen en portable geslaagd; 483 runtimebestanden gecontroleerd.
- `artifacts/clean-restoration-093/result.json`: schone installatie geslaagd
  op dezelfde commit, eveneens 483 runtimebestanden gecontroleerd.
- `artifacts/restoration-093-final3-package.log`: installer en portable verpakt.

Deze bewijsbestanden zijn op 3 november 2026 opnieuw gelezen. Dit is geen nieuwe
testrun of verklaring over de huidige installatie. Praktijkacceptatie blijft apart.

## Tweede lokale update: 0.9.4

De voorwaarde (installatie van 0.9.3) is voldaan. Op 3 november 2026 is hervatten
opgedragen: eerst deze voortgang en de bestaande broncommits met GitHub
synchroniseren, daarna punten 8–25 uitvoeren. Alle werkzaamheden en controles
blijven op de achtergrond, zonder desktopovername.

- [ ] 8. Vrij draadlabel.
- [ ] 9. Parallelle kopieën met aantal/afstand en voorstel.
- [ ] 10. Visuele oversteekbogen.
- [ ] 11. Contextmenu's.
- [ ] 12. Meer snapdoelen en eigen endpoint-schakelaar.
- [ ] 13. Zelfstandige herbruikbare selectieblokken.
- [ ] 14. Ader/splice dupliceren.
- [ ] 15. Spiegelen, roteren en tekenvolgorde.
- [ ] 16. Label/leaderhandvatten, pijlgrootte en tekstkader.
- [ ] 17. Vrije tabellen en opmaak van gekoppelde tabellen.
- [ ] 18. Tekst/afbeeldingen plakken.
- [ ] 19. Uitgebreid titelblok en bladzones.
- [ ] 20. Pinfunctie, netnaam, afscherming, tegenonderdelen, aansluitnotities.
- [ ] 21. Vrije STEP-previewcamera.
- [ ] 22. Bibliotheekverwijdering met herstel.
- [ ] 23. Panelen, thema, interfaceschaal en nieuwe-objectstandaarden.
- [ ] 24. Bevestigbare verbindings- en aftakkingsvoorstellen.
- [ ] 25. Grafische knopen en fysieke aansluitbare referentiepunten.
- [ ] Schema 4, migraties, regressies en exports getest.
- [ ] Verborgen WPF-ketentest en prestatiedrempels geslaagd.
- [ ] Geteste commit verpakt, upgrade en portable gecontroleerd, geïnstalleerd.

## Blijvende releasevoorwaarden

Presentatiebewerking verandert geen fysieke aantallen, aansluitingen of
productielengtes. Splitsen van tekengeometrie voegt geen materiaal toe.
Een fysieke splitsing kopieert nooit stilzwijgend de volledige kniplengte.
Ongeldige bewerkingen worden atomisch afgewezen. Annuleren verandert het
document en de undo-/redo-geschiedenis niet.

Automatische tests gebruiken synthetische gegevens. Bedrijfsbestanden blijven
buiten de repository. Een test met J904 gebruikt een afzonderlijke kopie.
Geen globale muis-/toetsenbordinvoer, focusovername of wijziging van het echte
klembord tijdens tests. Gebruikersprocessen worden niet geforceerd gesloten.

De prestatiedrempels blijven 33 ms p95 pan/zoom en 100 ms p95 gewone bewerkingen
op het blad met 100 connectoren en 1.000 aders. Releasebestanden komen uitsluitend
uit een schone, definitief geteste commit. Alleen lokale distributie; versie 1.0
vereist nog praktische beoordeling en de tweede-pc-proef.
