# Synkroniserede lyrics — design

Dato: 06-10-2026 · Branch: `feature/synced-lyrics` · Status: godkendt i brainstorming (arkitektur A, afsnit 2 og 3)

## Mål

Widget-kortet følger sangen: den linje, der synges nu, er fremhævet, med forrige linje(r) svagt over og de næste
linjer under. Kortet skifter linje af sig selv, mens panelet er åbent, og retter sig ind ved hver genkendelse.

## Brugerbeslutninger

| Spørgsmål | Valg |
|---|---|
| Visning | Fremhævet aktuel linje + kontekst (forrige svag, aktuel fed, næste normal) |
| Sange kun med ren tekst (ingen tidsstempler) | Vis fra begyndelsen, ingen fremhævning |
| Arkitektur | A: orchestratoren ejer et lyrics-ur; ren, testbar linjeberegning |

## Scope

Med: position fra ACRCloud, anker og linjeur i orchestratoren, linjevindue, ny lyrics-template med budget pr.
størrelse, provider-gengivelse ved linjeskift, tests, live-verifikation.

Ude (backlog): hurtigere genkendelse præcis ved sangskift; estimeret position for ren tekst; justerbar
forsinkelse i UI; small-størrelse.

## Komponenter

| Enhed | Ansvar | Afhænger af |
|---|---|---|
| `SongIdentificationService` | Udfylder `SongInfo.PositionAtSampleEnd` ud fra ACRCloud-offsets og klippets længde | ACRCloud-svar, WAV-header |
| `SyncedLyricsCursor` (ny, ren, statisk) | Aktuel linje og tid til næste linje for en given position | `LyricsLine`-listen |
| `MusicRecognitionOrchestrator` | Holder anker og linjeur; udsender `LinePositionChanged` | `TimeProvider`, cursor |
| `LyricsWindow` (ny, ren, statisk) | Bygger forrige/aktuel/næste-tekster til kortet | linjer + indeks |
| `WidgetStateManager` | Laver kortdata, inkl. vindue eller ren tekst | `LyricsWindow` |
| `LyricsTemplate.json` | Tegner pr. størrelse den synkroniserede trio eller ren tekst | data-felterne |
| `LyricsWidgetProvider` | Gengiver kortet igen ved `LinePositionChanged` (samme vej som `StateChanged`) | orchestrator |

## Position i sangen (anker)

1. `SongIdentificationService` beregner klippets længde fra WAV-headeren (data-størrelse ÷ byte rate).
2. Position ved klippets slutning:
   - hvis `db_end_time_offset_ms` og `sample_end_time_offset_ms` findes:
     `db_end_time_offset_ms + (klippets længde − sample_end_time_offset_ms)`;
   - ellers `play_offset_ms`;
   - ellers ukendt (`null`) → sangen behandles som uden synkronisering.
   - Klemmes til `[0, duration]`, når varigheden kendes.
3. Orchestratoren tager et tidsstempel (`TimeProvider.GetTimestamp()`), lige når optagelsen er færdig.
   Anker = (position ved klippets slutning, tidsstempel). Position nu = ankerets position + forløbet tid siden
   tidsstemplet. Svartid hos ACRCloud og LRCLIB regnes dermed automatisk med.

**Første implementeringstrin er at verificere tolkningen** mod rigtige ACRCloud-svar (proben logger felterne,
mens musik spiller): de to formler skal stemme overens inden for ca. 0,5 s. Afviger de, revurderes formlen, før
resten bygges.

## Linjeur

- `SyncedLyricsCursor.IndexAt(lines, position)`: indekset på sidste linje, hvis `Timestamp − LineLead ≤ position`;
  `-1` før første linje. `TimeUntilNextLine(lines, position)`: tid til næste linjes `Timestamp − LineLead`;
  `null` efter sidste linje. `LineLead = 300 ms` (navngiven konstant): linjen vises lidt før, så forsinkelsen
  frem til panelet opvejes.
- Uret kører kun, mens `State == DisplayingLyrics`, sangen har synkroniserede linjer, og et anker findes.
- Én engangs-timer (`TimeProvider.CreateTimer`), sat til næste linjeskift. Ved udløb beregnes linjen forfra ud fra
  uret (aldrig "+1"), `CurrentLineIndex` opdateres, `LinePositionChanged` udsendes, hvis indekset ændrede sig, og
  næste timer sættes.
- Stop: efter sidste linje, eller når positionen passerer sangens varighed; kortet bliver stående på sidste linje.
- Anker, timer og `CurrentLineIndex` beskyttes af en lås i orchestratoren; hændelser udsendes uden for låsen.

### Samspil

| Hændelse | Linjeur |
|---|---|
| Ny sang vist (`DisplayingLyrics`) | Nyt anker; uret startes |
| Genkendelse af samme sang (lydløs) | Ankeret flyttes; ændrer det linjen → `LinePositionChanged`; timeren sættes igen |
| Tilstand forlader `DisplayingLyrics` (fejl, NoMatch, ny sang hentes) | Uret stoppes |
| `Pause()` (panel lukket) | Uret stoppes, ankeret bevares |
| `Resume()` (panel åbnet) | Uret fortsætter fra det gamle anker; den straks-startede genkendelse retter positionen |
| `Stop()` | Anker og ur ryddes, `CurrentLineIndex = null` |

## Visning

Budget kalibreret mod et rigtigt medium-kort (06-10-2026): 6 lyrics-linjer efterlod ca. 70 px fri plads ved
16,4 px linjeafstand → medium rummer ca. 10 linjer. Large (≈ 300×462) rummer ca. 17.

| Størrelse | Synkroniseret: forrige / aktuel / næste | Ren tekst |
|---|---|---|
| Medium | 1 / højst 2 / 6 (≤ 9 linjer, 1 i reserve) | 9 linjer |
| Large | 3 / højst 2 / 11 (≤ 16 linjer) + "Source" | 15 linjer + "Source" |

Datafelter (altid alle til stede, tomme strenge hvor ubrugt, så `$when` er deterministisk):
`songTitle`, `artist`, `source`, `plainLyrics` (højst 15 linjer; også "No lyrics found for this song."),
`previousLineMedium` (1 linje), `previousLinesLarge` (3 linjer), `currentLine`, `nextLines` (11 linjer).

Template: pr. størrelse (`$when` på `$host.widgetSize`) enten en ren-tekst-blok (`plainLyrics != ''`) eller
trioen (`currentLine != ''`): forrige (`isSubtle`), aktuel (`Bolder`, `maxLines` 2), næste. Tomme blokke skjules med
`$when`. Titel og kunstner `maxLines` 1. Knappen "Listen Again" bevares.

Orchestratoren eksponerer `int? CurrentLineIndex` (værdi kun når sangen har synkroniserede linjer og et anker; `-1`
før første linje). `WidgetStateManager.GetWidgetContent` får indekset som parameter: med værdi → trioen via
`LyricsWindow`; uden → `plainLyrics`.

Særtilfælde: før første linje er `currentLine = "♪"` og `nextLines` = de første linjer; efter sidste linje er
`nextLines` tom. Uden anker eller uden synkroniserede linjer bruges `plainLyrics`.

## Fejlhåndtering

Manglende offsetfelter giver en ukendt position (ren tekst), ingen fejl. Linjeurets callback lader aldrig en
undtagelse slippe ud (en ufanget undtagelse i en timer-callback vælter processen): kaster en
`LinePositionChanged`-handler (fx `UpdateWidget`), behandles det som en fejl i en genkendelsescyklus i dag —
`ErrorMessage` sættes, tilstanden bliver `Error`, `ErrorOccurred` udsendes én gang, og uret stopper, fordi tilstanden
har forladt `DisplayingLyrics`. Næste genkendelse af samme sang genopretter visningen og uret. Ingen nye netværkskald.

## Test

- Position: begge formler, manglende felter, klemning til varighed, klippelængde fra WAV-header.
- `SyncedLyricsCursor`: før første linje, præcis på grænsen med 300 ms forspring, midt i, efter sidste linje.
- `LyricsWindow`: start, midte, slut, intro ("♪"), ren tekst, "No lyrics found".
- Orchestrator med `FakeTimeProvider` (Microsoft.Extensions.TimeProvider.Testing, kun i testprojektet):
  linjehændelser til rette tid; samme sang flytter ankeret; Pause stopper, Resume genstarter, Stop rydder;
  stop efter sangens slutning; intet ur uden synkroniserede linjer eller uden anker; uret stopper ved fejl.
- Template-layout: ingen blok uden størrelse-`$when`; summen af `maxLines` pr. størrelse inden for budget
  (medium ≤ 9, large ≤ 16 + kilde); alle `${felt}` findes i data.
- Live: proben logger ACRCloud-offsetfelter (verifikation af formlen); slutkontrol på kortet med rigtig musik.

## Risici

- Panelet kan begrænse hyppige `UpdateWidget`-kald (udokumenteret). Opdatering sker kun ved linjeskift (typisk hver
  2–5 s) og kun mens panelet er åbent. Viser det sig at drosle, indføres et minimumsinterval.
- Windows' tekstskalering over 100 % kan sprænge budgettet; der er 1 linjes reserve.
- `LineLead` er et skøn og justeres efter live-test.
