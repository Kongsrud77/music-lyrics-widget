# MusicLyricsWidget

Windows 11-widget (Widgets Board), der lytter via mikrofonen, identificerer sangen med ACRCloud og viser lyrics fra LRCLIB.
C# / .NET 8 / WinUI 3 / Windows App SDK 1.5, MSIX-pakket, kun x64. Ligger under `C:\Python`, men er IKKE et Python-projekt.

## Build & deploy

- Build: `dotnet build MusicLyricsWidget/MusicLyricsWidget.csproj -c Debug -p:Platform=x64` (solution har kun x64)
- Pakken er dev-registreret direkte fra `MusicLyricsWidget\bin\x64\Debug\net8.0-windows10.0.22621.0\AppX` (loose-file deploy fra VS).
  En build overskriver den installerede version; låste filer = widget-provider-processen kører stadig.
- Ændringer i `Package.appxmanifest` slår først igennem efter re-registrering (VS "Deploy" eller
  `Add-AppxPackage -Register <AppX>\AppxManifest.xml`).
- Byg en kopi i scratchpad, hvis den installerede version ikke må røres.
- Ingen testprojekt endnu. Ingen git-repo endnu.

## Arkitektur

- `Program.cs` — én exe, to tilstande: `-RegisterProcessAsComServer` → COM-server for Widget Host; ellers WinUI-indstillingsvindue.
- `Widget/LyricsWidgetProvider.cs` — `IWidgetProvider`; ejer orchestratoren; håndterer kun ét widget-id (`_activeWidgetId`).
- `Widget/WidgetStateManager.cs` — `RecognitionState` → (Adaptive Card-template, data-JSON).
- `Services/MusicRecognitionOrchestrator.cs` — Timer-loop (30 s): capture → identify → lyrics. Events `StateChanged`/`ErrorOccurred`.
- `Services/AudioCaptureService.cs` — NAudio `WaveInEvent`, 10 s, 44,1 kHz mono 16-bit + `WrapInWav`.
- `Services/SongIdentificationService.cs` — ACRCloud `POST /v1/identify`, HMAC-SHA1 signatur v1 (verificeret mod ACRCloud-docs).
- `Services/LyricsService.cs` — LRCLIB `/api/search` + LRC-parser.
- `MainWindow.xaml(.cs)` + `SettingsManager.cs` — companion-app: gem ACRCloud-nøgler, "Test Recognition"
  (duplikerer pipelinen uden orchestrator og dumper debug-info).

## Widget/COM-invarianter

- CLSID `3B37F72B-9C04-418C-88AE-BEB6058AE758` står 3 steder: `Program.cs`, manifestets `com:Class Id` og `CreateInstance ClassId`. Skal være identiske.
- Class factory skal returnere `MarshalInspectable<IWidgetProvider>.FromManaged(...)` (Microsofts reference).
  `Marshal.GetIUnknownForObject` giver en klassisk COM-CCW, ikke en WinRT-vtable. `Program.cs` bryder p.t. denne regel.
- Hver `<Definition>` kræver `<ThemeResources><Screenshots>` (Required ifølge Microsofts manifest-format); uden den vises widgetten ikke i "Tilføj widgets".
- `AllowMultiple` er som standard `true`, men provideren håndterer kun ét widget-id. Sæt `false`, eller gør provideren multi-instans.
- Verbet `listenNow` er hardcodet i alle tre templates og i `OnActionInvoked`. Et nyt verb kræver ændring begge steder.
- Template-felter (`${statusText}`, `${songTitle}`, `${artist}`, `${lyrics}`, `${source}`, `${errorMessage}`) skal matche
  de anonyme objekter i `WidgetStateManager`. Templates læses fra `AppContext.BaseDirectory\Widget\WidgetTemplates`.
- `WidgetManager.UpdateWidget` kaldes fra Timer- og NAudio-tråde. Hold provider-state trådsikker.

## Config

- `%LOCALAPPDATA%\MusicLyricsWidget\settings.json` (Newtonsoft) deles mellem companion-appen og widget-processen.
  Widgetten genindlæser ved hvert "Listen Now".
- Access secret ligger i klartekst. Vis/log aldrig filens indhold; flyt den til DPAPI (`ProtectedData`) eller `PasswordVault`.

## Eksterne API'er

- ACRCloud: `status.code` 0 = match, 1001 = ingen match. Alle andre koder er fejl (nøgle/kvote/signatur) og skal vises.
  Docs: sample bør være < 15 s og < 1 MB (hård grænse 5 MB). 10 s × 44,1 kHz mono ≈ 882 KB.
  `play_offset_ms` (afspilningsposition) er nøglen til synkroniserede lyrics; den bliver ikke parset endnu.
- LRCLIB: `/api/search` returnerer mange versioner af samme sang med forskellig varighed (fx 263–359 s for én titel).
  Brug `/api/get?artist_name=&track_name=&duration=` med ACRCloud-`duration_ms` først og search som fallback.

## Diagnose

- Widgetten mangler i "Tilføj widgets": manglende Screenshot, eller pakken er ikke re-registreret efter en manifest-ændring.
- Widgetten tilføjes, men forbliver tom eller crasher ved klik: COM-aktivering (class factory, apartment) eller templates mangler i AppX.
- Viser altid "Ready — tap Listen Now": ACRCloud returnerede en status ≠ 0, som p.t. behandles som "ingen musik".
  Brug "Test Recognition" i companion-appen; den viser den rå respons.
