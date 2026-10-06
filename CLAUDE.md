# MusicLyricsWidget

Windows 11-widget (Widgets Board), der lytter via mikrofonen, identificerer sangen med ACRCloud og viser lyrics fra LRCLIB.
C# / .NET 8 / WinUI 3 / Windows App SDK 1.5, MSIX-pakket, kun x64. Ligger under `C:\Python`, men er IKKE et Python-projekt.

## Build, test, deploy

- Build: `dotnet build MusicLyricsWidget.sln -c Debug -p:Platform=x64`
- Test: `dotnet test MusicLyricsWidget.Tests/MusicLyricsWidget.Tests.csproj -c Debug -p:Platform=x64` (xUnit, ingen netværk/mikrofon/WinRT)
- Testprojektet compiler appens rene kilder via linkede `<Compile Include>` (Models/*, Services/*, WidgetStateManager,
  WidgetProviderIds, LaunchArguments) — ikke en ProjectReference. `internal` er derfor synligt i tests.
  Link ALDRIG WinRT/WinUI-filer (`Program.cs`, `App.xaml.cs`, `MainWindow.xaml.cs`, `LyricsWidgetProvider.cs`).
- Pakken er dev-registreret fra `MusicLyricsWidget\bin\x64\Debug\net8.0-windows10.0.22621.0\AppX` (loose files).
  `dotnet build` skriver IKKE til `AppX`; kun en deploy (VS "Deploy" eller layout + `Add-AppxPackage -Register`) gør.
- Manifest-ændringer slår først igennem efter re-registrering.

## Arkitektur

- `Program.cs` — én exe, to tilstande. `LaunchArguments.IsComServerLaunch(args)` (`-RegisterProcessAsComServer`, `-Embedding`)
  → COM-server på en dedikeret MTA-tråd; ellers WinUI-indstillingsvinduet (STA). Processen afslutter, når sidste widget slettes.
- `Widget/LyricsWidgetProvider.cs` — `IWidgetProvider`; ÉN delt instans pr. proces (`Lazy`, `PublicationOnly`); ét widget-id.
- `Widget/WidgetStateManager.cs` — `RecognitionState` → (Adaptive Card-template, data-JSON).
- `Services/MusicRecognitionOrchestrator.cs` — Timer-loop (30 s): capture → identify → lyrics. Interfaces `IAudioCapture`,
  `ISongIdentifier`, `ILyricsProvider` gør den testbar med fakes.
- `Services/AudioCaptureService.cs` — 10 s mono 16-bit pr. optagelse via `RawMicrophoneCapture` (NAudio `IWaveIn`);
  disposes altid; samtidige optagelser afvises.
- `Services/RawMicrophoneCapture.cs` + `CoreAudioInterop.cs` — WASAPI i RAW-mode (ingen driver-støjdæmpning; WASAPI
  konverterer til 44,1 kHz). Laptoppens Intel Smart Sound-array klipper ellers musik til digital stilhed (målt: 50–98 %
  nul-samples, aldrig match). Brug ALDRIG NAudios `IAudioClient2` (forkert vtable-plads → 0x88890001); se interop-laget.
- `Services/SongIdentificationService.cs` — ACRCloud `POST /v1/identify`, HMAC-SHA1 signatur v1 (verificeret).
- `Services/LyricsService.cs` — LRCLIB `/api/get` → `/api/search`-fallback + LRC-parser.
- `MainWindow.xaml(.cs)` + `SettingsManager.cs` — companion-app (nøgler + "Test Recognition" med debug-dump).

## Widget/COM-invarianter (pinnet af ManifestTests/TemplateVerbTests)

- `WidgetProviderIds` er eneste kilde til CLSID, verb `listenNow` og `ComServerArgument`. Manifestets `com:Class Id`,
  `CreateInstance ClassId` og `com:ExeServer Arguments` skal matche. Mangler `Arguments`, åbner COM indstillingsvinduet i stedet for serveren.
- Class factory: `MarshalInspectable<IWidgetProvider>.FromManaged(...)` + `Marshal.QueryInterface(riid)`; aldrig `Marshal.GetIUnknownForObject`.
- `<Definition>`: `AllowMultiple="false"` og `<Screenshots>` (300×304 RGBA, transparente hjørner) er påkrævet.
- Template-felter (`${statusText}`, `${songTitle}`, `${artist}`, `${lyrics}`, `${source}`, `${errorMessage}`) = data i `WidgetStateManager`.
- Provider-tråde: `_gate` beskytter `_activeWidgetId`/`_orchestrator`. Kald ALDRIG `WidgetManager.UpdateWidget` eller orchestrator
  `Start`/`Stop`/`Dispose` under låsen (Stop udsender Idle synkront). Events fra en udskiftet orchestrator ignoreres (`ReferenceEquals`).
- Panel lukker → `Deactivate` → orchestrator `Pause()`: mikrofonen slukker (privatliv), men sang/lyrics/tilstand bevares.
  Panel åbner → `Activate` viser den bevarede tilstand og kalder `Resume()`, som KUN genoptager et pauset loop
  (aldrig trykket Listen Now → "Ready"). `Stop()` (slet widget, ny Listen Now) nulstiller og ophæver pausen.

## Fejlsemantik (end-to-end)

- Services returnerer `null` KUN for "intet resultat": ACRCloud 1001 og 2004 (stilhed), LRCLIB 404/ingen lyrics, tom titel/kunstner.
  Alt andet kaster (`AcrCloudException` med `StatusCode`, `HttpRequestException`, JSON-fejl). Beskeden vises i widgettens fejlkort.
- Orchestratoren: `OperationCanceledException` er kun stille, når cyklussens egen token er annulleret (en HttpClient-timeout = fejl).
  Mens en sang vises, kører cyklusser lydløst (ingen flimmer). Lyrics ryddes efter `MissesBeforeClearingLyrics` (2) misses → `NoMatch`.
  Et lyrics-opslag, der kastede, prøves igen ved næste match af samme sang.

## Config

- `%LOCALAPPDATA%\MusicLyricsWidget\settings.json` (Newtonsoft) deles mellem companion-app og widget-proces.
- Access secret ligger i klartekst. Vis/log aldrig filens indhold; flyt den til DPAPI (`ProtectedData`) eller `PasswordVault`.

## Eksterne API'er

- ACRCloud: 0 = match, 1001 = intet match, 2004 = kunne ikke lave fingerprint (stilhed). Alle andre koder er fejl. Sample < 15 s og < 1 MB.
  `play_offset_ms` (afspilningsposition) er nøglen til synkroniserede lyrics; den bliver ikke parset endnu.
- LRCLIB: `track_name`/`artist_name` er påkrævet, `album_name` og `duration` (1–3600 s) er valgfri. 404 `TrackNotFound`, 503 `ServerOverloaded`.
  `/api/search` returnerer mange versioner med forskellig varighed; vælg den nærmeste varighed og foretræk synced.

## Diagnose

- Widgetten mangler i "Tilføj widgets": manglende Screenshot, eller pakken er ikke re-registreret efter en manifest-ændring.
- Indstillingsvinduet popper op, når widgetten tilføjes: `com:ExeServer Arguments` mangler eller matcher ikke `ComServerArgument`.
- Kortet viser "No music recognised yet": ingen match, eller mikrofonen leverer stilhed. Tjek om RAW faktisk blev brugt
  (`RawMicrophoneCapture.IsRaw`) og Windows' privatlivsindstillinger for mikrofonen. En probe der måler andelen af
  nul-samples afslører en støjgate (rigtig rumstøj er aldrig præcis 0).
- Windows logger sidste mikrofonbrug pr. app: `HKCU\...\CapabilityAccessManager\ConsentStore\microphone\<PFN>`
  (`LastUsedTimeStart/Stop`) — viser om widget-processen overhovedet optager.
- Deploy af kodeændringer: stop ALLE `MusicLyricsWidget.exe`-processer, kopiér ændrede filer til `AppX` (layout ud fra
  `MusicLyricsWidget.build.appxrecipe`); `resources.pri` kan være memory-mappet af systemet → overskriv kun ændrede filer.
- Fejlkort med "ACRCloud error 3001/3014": nøgler eller signatur. Brug "Test Recognition" i companion-appen, som viser den rå respons.
- Backlog og afgørelser fra fix-runden: `docs/plans/2026-10-02-widget-p0-p1.md`.
