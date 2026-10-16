# ClassScribe

A .NET 10 console app for personal study. During an online class it records whatever the laptop
plays, by WASAPI loopback: the output device the class plays on, a device pinned by name, or one
app's sound through process loopback. It does not record the microphone yet. When stopped, it sends
the recording to ElevenLabs Scribe v2 with a glossary of Arabic terms and saves a Markdown
transcript and ElevenLabs' JSON answer beside the recording. `README.md` is the user guide. The full
background, design decisions and history are in `docs/HANDOFF.md` (kept out of git); read it before
larger changes. It has not been brought up to date with the review fix round or with increment 1
(settings read before each run, rebuild and the transcribe guards, the growing upload limit, the
arrival watch, keep-awake, the device check's COM fix) yet, section 6.4 above all, so where it
disagrees with the code or with this file, the code wins.

## Ground rules

- Never add anything that downloads from the course website or gets around its protection.
  ClassScribe only records audio the user is already playing. Keep it that way.
- The ElevenLabs API key lives only in the `ELEVENLABS_API_KEY` environment variable (user
  scope). Never print, log, write to a file or commit it, and never put it in `appsettings.json`
  or tests. To check it, print only whether it is set:
  `if ([Environment]::GetEnvironmentVariable('ELEVENLABS_API_KEY','User')) { 'set' } else { 'missing' }`.
  Terminals started before the variable was set do not see it: close and reopen the terminal, and
  for VS Code quit every VS Code window and start it again (a new terminal inside a running VS Code
  keeps the old environment). The app reads the key from that variable alone
  (`ApiKeyFromEnvironment`, the last configuration source); `SettingsCheck` warns about a key set
  anywhere else.
- A real transcription request spends the user's ElevenLabs credits and sends audio to
  ElevenLabs, so ask before making one. Tests use `StubHttpHandler`; manual runs can point
  `ElevenLabs__BaseUrl` at a local stub.
- Do not turn on trimming or Native AOT: NAudio reaches Windows audio through COM interop.

## Conventions

- .NET 10 and C# 14, nullable on, file-scoped namespaces, primary constructors, records and
  `init` for immutable data, target-typed `new`, collection expressions, LINQ where it reads
  well, async/await, the built-in DI container.
- HTTP APIs are called through Refit interfaces registered with `AddRefitGeneratedClient` (Refit's
  source-generated client, given the `ElevenLabsJson` System.Text.Json context), not requests built
  by hand with `HttpClient`.
- No `ArgumentNullException` guards on DI-injected parameters.
- Registration: each area registers its services in its own `<Area>Services.cs` (`SettingsServices`,
  `StorageServices`, `AudioServices`, `TranscriptionServices`, `DesktopServices`, `CommandServices`),
  called from `ServiceCollectionExtensions.AddClassScribe`; `AppHost` adds the settings reload
  (`SettingsServices.AddSettingsReload`), since it builds the configuration the reload works on.
  `AppHost.Build` validates scopes and every registration (`ValidateScopes`, `ValidateOnBuild`), so
  a per-run service taken outside a run's scope, or one that cannot be made, fails at once.
- Settings in code: singletons read `IOptionsMonitor<T>.CurrentValue` each time they use it
  (`ClassFolders`, `TranscriptFormatter`, `SourceChoice`, the Refit client's `ConfigureHttpClient`);
  per-run services, scoped or transient (the commands, `TranscriptionWorkflow`, `ElevenLabsClient`,
  `RecordingSession`), take `IOptionsSnapshot<T>`, read once in the run's scope. Never `IOptions<T>`
  for ClassScribe's sections: it keeps the first value for the life of the process, so an edit
  would never reach that part (`No_part_keeps_the_settings_it_read_first_for_the_whole_session`
  fails on one).
- XML `<summary>` comments on every class and method, tests included. Both projects set
  `GenerateDocumentationFile`, so a public type or member without one gives CS1591: an error in the
  test project (`WarningsAsErrors`), a warning in the app (`ClassScribe.xml` is not published, as
  `PublishDocumentationFile` is off). Keep the build at 0 warnings. The compiler does not check
  private or internal members, so check those yourself; a local function cannot take a summary
  (CS1587), so give it a `//` comment.
- Errors: log at each layer with the existing logger; rethrow with `throw;` (never `throw ex;`);
  never swallow exceptions. A deliberate exception, a catch that does not rethrow, carries a
  comment that starts `// Deliberately not rethrown:` and gives the reason, so
  `grep -rn "Deliberately not rethrown" src` lists them. A few older ones give their reason in other
  words or in the method's summary: `App.RunAsync`, the Ctrl+C catch in
  `MenuCommand.RunChoiceAsync`, `AudioPreparer.DeleteUnfinished`, `RecordingSession.RestartAsync`,
  `WasapiLoopbackAudioCapture.CaptureLoop`, `TranscriptionWorkflow.TryPrepare` and the retry catch in
  `ElevenLabsClient.TranscribeAsync`. Use the phrase for every new one and hold it to the same
  standard. `docs/HANDOFF.md` section 6.4 lists them too, but is out of date until it is refreshed;
  until then the comments are the list.
- Windows audio objects: the device check (`WasapiLoopbackCaptureProvider`'s two listings, once a
  second for the whole class) goes through ClassScribe's own declarations in
  `Audio/Windows/CoreAudioInterop.cs` and releases every COM object it receives in a `finally` block
  (`CoreAudio.Release`). Never make Windows' device enumerator with `new` of a `[ComImport]` class or
  through a cached wrapper; use `CoreAudio.CreateEnumerator`. Windows hands out one enumerator per
  process, and a second wrapper type for it makes NAudio's `new MMDeviceEnumerator()` throw
  `InvalidCastException`.
- Time every interval on the steady clock (`TimeProvider.GetTimestamp` and `GetElapsedTime`), never
  the time of day, so a change of the Windows clock neither stretches nor shortens it. In tests,
  `ManualTimeProvider.Advance` moves both clocks and `StepWallClock` only the time of day.
- TDD with NUnit and Moq: write the failing test first. Run `dotnet test` before handing code over.
  For the real host with stand-ins, use `AppHost.Build(contentRoot, apiKey, configureServices)`;
  `configureServices` runs last. `TestDoubles.cs` has `TestOptions<T>` (one object for `IOptions`,
  `IOptionsSnapshot` and `IOptionsMonitor`; `Set` fires `OnChange`) and `TestSettings` (a real gate
  over a temporary `appsettings.json`). Write hours of audio with `WavSegmentWriter` at 10 Hz, so
  the files stay under a megabyte.
- Keep `README.md` in step when behaviour changes, including its troubleshooting rows when a log
  message is added or changed.

## Working with Gulzar

- Balanced answers: not terse, not padded. No em dashes.
- When questions are needed, ask them all at once, as multiple choice where possible.
- Cite sources for research and other non-code answers.
- Arabic terms in IJMES transliteration (this governs `src/ClassScribe/keyterms.txt`).

## Commands (PowerShell, from the repo root)

```powershell
dotnet build
dotnet test                                                            # with the hang guard (see below)
dotnet test --blame-hang-timeout 2m                                    # also names a test that hangs
dotnet test --filter TestCategory=WindowsAudio                         # real device list, meters, sessions
dotnet test --filter TestCategory=WindowsPower                         # a real power request, let go at once
dotnet test --filter TestCategory=AudioDevice                          # Explicit tests on real devices
dotnet run --project src/ClassScribe                                   # the menu
dotnet run --project src/ClassScribe -- record "Kalam"                 # record; Q twice stops and transcribes
dotnet run --project src/ClassScribe -- record-only "Kalam"            # record only
dotnet run --project src/ClassScribe -- transcribe "C:\path\to\folder-or-file"   # --no-open opens nothing
dotnet run --project src/ClassScribe -- transcribe "<path>" --again    # one with a transcript; billed again
dotnet run --project src/ClassScribe -- rebuild "C:\path\to\folder-or-file"      # free, from the saved .json; --no-open
dotnet publish src/ClassScribe -c Release -r win-x64 --self-contained false -o publish
```

- `transcribe` on the command line (and a file dropped on the exe) refuses, with exit 1, a recording
  that already has a transcript or a kept paid answer unless `--again` is given, and prints the free
  `rebuild` command and the `--again` one, each runnable as printed (`CommandLine.Rebuild`,
  `CommandLine.Transcribe(path, again: true)`); menu choices 3 and 4 ask R (rebuild) or Y (transcribe
  again) instead. Every caller refuses a recording that is still being written
  (`ClassFolders.IsBeingWritten`), and `rebuild` refuses one without a saved answer. `--no-open`
  works with both commands; options go before or after the path, in any case (`App`).
- The `WindowsAudio` and `WindowsPower` tests run in the normal `dotnet test` too; the filters above
  run them alone. `WindowsAudio` only reads Windows' device list, level meters and audio sessions:
  `DeviceCheckResourceTests` (1,000 device listings and 300 app listings in a no-GC region, allowed
  at most 20 handles and 5 MB of native memory), `DeviceCheckListingTests`, `CoreAudioInteropTests`
  (both orders of making the two enumerators), and one `RecordingSessionTests` test that opens the
  default device's audio client only to read its format. `WindowsPower` makes a real power request
  and lets it go at once, and reads the power source and Windows' interrupt times
  (`WindowsPowerRequestsTests`, `WindowsSystemTimesTests`). Nothing is played or recorded. The suite
  had 887 tests at the end of increment 1.
- The hang guard: `tests/ClassScribe.Tests/ClassScribe.Tests.runsettings` sets `TestSessionTimeout`
  to 10 minutes, and the test csproj names it in `RunSettingsFilePath`, so a plain `dotnet test`
  aborts a run that deadlocks and fails it instead of hanging. The suite normally takes well under a
  minute. `[CancelAfter]` cannot interrupt a blocked `Wait`, so it is no guard on its own.
- The two `AudioDevice` tests in `WindowsCaptureTests` are `[Explicit]`: they open the laptop's real
  output devices (listening only), so run them only when the user is not in a class or an audio test.
- Publish as a folder, as above. Bitdefender quarantined earlier single-file builds
  (`-p:PublishSingleFile=true`) as Gen:Variant.Yogi.67941, a false positive; the README keeps the
  single file as an option with that caveat.
- While recording: Q twice within 5 s stops (the first Q logs "Press Q again within 5 seconds to
  stop"); Ctrl+C stops at once, a second Ctrl+C within 2 s of it is part of the same stop, and a
  later one cancels the preparation and upload; P pauses or resumes; M writes a mark to the
  recording's `.marks.txt`.
- What is recorded (`CaptureChooser`): by default the output device with sound, found with Windows'
  level meters once a second; `Recording:Device` pins a device; `Recording:App` records one app's
  process tree through Windows process loopback (`ProcessLoopback`). A pick from menu choice 6,
  automatic included, holds until ClassScribe is closed (`SourceChoice`); without one, the settings
  decide as they are read again. ClassScribe's own processes, this one and any other ClassScribe,
  are left out of the apps with sound (a recording's own capture is a session too), so
  `Recording:App` cannot pick ClassScribe.
- Arrival watch: `RecordingSession.GetStatus` also reports `SinceLastPacket` (since the open capture
  last handed over any audio, silence and pauses included), `DeliversContinuously` (true for process
  loopback, false for device loopback) and `RecordedLevel` (Windows' meter for the recorded device or
  app at the last look; null while paused, after a failed look, or when it is not listed).
  `RecordCommand.WatchArrival` warns when an app's capture has handed over nothing for 5 s, or a
  device's meter has shown sound at every tick for 10 s while nothing arrived, at most every 30 s,
  and calls `RecordingSession.Reopen(reason)`, which replaces the open capture at the next look, in
  a new part, and logs the reason at Debug.
- Keep-awake: `IPowerRequests.Hold(StayAwake.RecordingReason, ...)` from the start of capture until
  the transcript is saved (`record-only`: until the files are closed), and
  `StayAwake.TranscribingReason` while `transcribe` prepares and uploads. `StayAwake.Needs` asks for
  the system and execution requests always, and the display as `Recording:KeepScreenOn` says
  (`OnBattery` by default), read again every 30 s with the power source. A refused request is logged
  once with advice, never thrown. `FreezeWatch` (the loop's ticks plus a 1-second timer of its own)
  logs sleeps and hold-ups of 3 s or more, told apart by Windows' interrupt time against its unbiased
  interrupt time; the arrival watch and the silence warning count from the end of the last one.
  Tests may make a real power request only if they let it go at once (the `WindowsPower` category),
  and must never change the computer's power settings; everywhere else use `FakePowerRequests` and
  `FakePowerSource`.
- Exit codes: 0 success; 1 failure, settings that fail the check included, and a refused command (a
  transcribed recording without `--again`, a recording still being written, `rebuild` without a
  saved answer); 2 unknown command, or a `transcribe` or `rebuild` line with no path, a second one,
  or an option that command does not take; 130 cancelled.
- Recordings go to `Recording:OutputFolder` (`%USERPROFILE%\ClassScribe`; a relative path is taken
  from the user profile, see `ClassFolders.Root`): a folder per recording name, and files named
  `<name>_<yyyy-MM-dd_HH-mm-ss>Z` (UTC start) with `.wav`, `_partN.wav`, `.marks.txt`, `.md` (the
  Markdown transcript), `.json` (ElevenLabs' answer), `.upload.wav` (the 16 kHz copy, removed after
  every try, failed or cancelled included, unless `Recording:KeepUploadFile`) and `.unread.json` (an
  answer ElevenLabs charged for that did not become a transcript; a `.json` left without its `.md`
  counts as one too, see `RecordingFiles.FindKeptAnswers`; menu choice 3 shows `answer kept` and
  asks before a charged retry). `RecordingFiles` owns the naming and `TranscriptCopies` the copy
  numbers: a transcript is never overwritten (`(2).md`, `(3).md`), not even by two processes at
  once, because each copy's `.json` and `.md` are claimed with `FileMode.CreateNew`. `HasTranscript`
  counts any copy.
- Transcription: `ElevenLabsAnswer` is the only reader of ElevenLabs' answers, for the client and for
  rebuild. `UploadTimeLimit` gives each try 10 minutes plus the upload at
  `ElevenLabs:SlowestUploadMbps` plus a quarter of the audio, rounded to the minute, never below
  `ElevenLabs:TimeoutMinutes` (2 h of audio: 71 minutes); `HttpClient.Timeout` is off, as
  `ElevenLabsClient` holds the limit. A recording longer than `TranscriptionWorkflow.LongestRecording`
  (9.5 h) is refused before anything is prepared. `TranscriptionWorkflow.RebuildAsync` lays the
  newest saved answer (never an `.unread.json` unless it is named) out again with the current marks
  and `Transcript` settings, as the next free copy, with no key and no request.
- Settings are in `src/ClassScribe/appsettings.json`, read from the exe's folder (under `dotnet run`,
  the copy in `bin\Debug\net10.0`; the menu names it and warns when the project's file is newer).
  `RunGate` reads the file again before each menu choice and each command (`SettingsReloader`): a
  staged copy is checked by `SettingsCheck`, and only values that pass reach the live settings,
  through `CheckedSettingsSource` and a reload; nothing watches the file. A refused file keeps the
  last accepted values: the menu shows the problems and refuses choices (the menu comes back), a
  command exits 1. It does not stop the start: the first checked read in `AppHost.Build` leaves the
  defaults in place. A file gone after it was accepted is refused the same way. A new setting needs
  its check in `SettingsCheck` and a row in the README. Environment overrides such as
  `ElevenLabs__BaseUrl` work; one that fails the check stops the start, since it cannot be fixed
  while ClassScribe runs. Warnings (a key in the settings, an output folder inside OneDrive) are
  given once per process. `SettingsReloader.Snapshot` gives the Recording, ElevenLabs (never the
  key), Transcript and Logging sections as they are now; only its test uses it so far.

## Layout

- `src/ClassScribe`: `Program.cs` (the console set to UTF-16, a start-up failure printed plainly,
  the close prompt), `AppHost` (builds the host: the checked settings source and a first checked
  read, the key from the environment, the plain log format, scopes and registrations validated),
  `App` (the command line, with the gate before each command), `ServiceCollectionExtensions` (calls
  each area's `<Area>Services.cs`), `Durations`, `PlainConsoleFormatter`.
- `src/ClassScribe/Settings`: the options classes, `ApiKeyFromEnvironment`, `SettingsCheck` (types,
  enums, ranges and paths, the Logging section, the warnings), and the reload: `SettingsFile`,
  `CheckedSettingsSource`, `SettingsReloader` and `SettingsServices`.
- `src/ClassScribe/Desktop`: keeping the laptop awake (`IPowerRequests`, `IPowerSource` and
  `StayAwake`, all in `IPowerRequests.cs`) and the computer's clocks (`ISystemTimes`);
  `Desktop/Windows` holds the Windows calls (`WindowsPowerRequests` with `WindowsPowerSource`, and
  `WindowsSystemTimes`). `DesktopServices` registers them and `FreezeWatch`.
- `src/ClassScribe/Recorder`: `FreezeWatch`.
- `src/ClassScribe/Audio`: capture (`Windows/` holds the WASAPI code), `RecordingSession`, WAV
  writing (each packet goes to Windows at once; the header is rewritten and flushed to disk every
  `Recording:FlushSeconds`), upload preparation. `Audio/Windows/CoreAudioInterop.cs` holds
  ClassScribe's own Core Audio declarations and `CoreAudio` (`CreateEnumerator`, `Check`, `Release`):
  the device check lists through them, and NAudio does the notifications, the device names and the
  captures.
- `src/ClassScribe/Transcription`: ElevenLabs client (a Refit interface plus `ElevenLabsClient`
  around it), the shared answer parser (`ElevenLabsAnswer`), the upload's time limit
  (`UploadTimeLimit`), glossary loader, transcript formatter, workflow (rebuild included).
- `src/ClassScribe/Storage`: class folders under the output folder, recording file names, numbered
  transcript copies (`TranscriptCopies`, never overwritten), and marks.
- `src/ClassScribe/Commands`: the menu, the `record`, `transcribe` and `rebuild` commands
  (`RebuildCommand`), the gate (`RunGate`), the console and desktop abstractions, the close prompt
  and the exit warnings. Each command runs in its own DI scope (`ICommandRunner`).
- `tests/ClassScribe.Tests`: NUnit and Moq. Hand-written fakes for the device, keyboard, clock and
  HTTP transport are in `TestDoubles.cs`, with `TestOptions<T>`, `TestSettings`,
  `FakeAudioCapture.DeliversContinuously` (default false, like device loopback), and
  `FakeCaptureProvider.SetAppLevel` and `FailListings`. Others are in `TestDoubles.*.cs`:
  `TestDoubles.CoreAudio.cs` (fakes of Windows' enumerator, devices and sessions, and
  `FakeComLedger`, which checks that every object handed out is released once),
  `TestDoubles.Power.cs` (`FakeSystemTimes`, `FakePowerRequests`, `FakePowerSource`,
  `FakePowerRequestApi`) and `TestDoubles.Commands.cs` (`ZonedTimeProvider`). `Denied.cs` holds ACL
  helpers; `ClassScribe.Tests.runsettings` holds the hang guard. The test project sees ClassScribe's
  internals (`InternalsVisibleTo` in `CoreAudioInterop.cs`).
