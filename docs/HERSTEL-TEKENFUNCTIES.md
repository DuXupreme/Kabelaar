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
- [ ] Geteste commit verpakt, upgrade en portable gecontroleerd, geïnstalleerd.

Ontwikkelcontrole: 191 tests (83 Core, 43 Infrastructure, 65 Rendering) en de
verborgen WPF-ketentest geslaagd. Bewijs onder `artifacts/restoration-093-preflight`
en `artifacts/restoration-093-smoke-dev2`. De definitieve releasebuild herhaalt
deze controles op de vastgelegde commit; ontwikkelbewijs vervangt dat niet.

De eerste definitieve pakketmeting overschreed de bewerkingsgrens bij wisselende
pc-belasting. Daarom blijven verpakking en installatie geblokkeerd tot een
volledige releasecontrole slaagt. De volgende optimalisatie indexeert betrokken
routes per transactie, controleert gedeelde schaalmaten in één doorgang en
vermijdt onnodige tekenallocaties. De bijbehorende 194 regressietests slagen;
bewijs staat onder `artifacts/restoration-093-optimized-tests`. Per bewerking
worden nu ook opdracht-, scène-, allocatie- en GC-metingen vastgelegd.

## Tweede lokale update: 0.9.4

Pas starten nadat de eerste update is geïnstalleerd.

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
