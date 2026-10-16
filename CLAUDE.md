# ClassScribe

A .NET 10 console app for personal study. During an online class it records whatever the laptop
plays, by WASAPI loopback: the output device the class plays on, a device pinned by name, or one
app's sound through process loopback. It does not record the microphone yet. When stopped, it sends
the recording to ElevenLabs Scribe v2 with a glossary of Arabic terms and saves a Markdown
transcript and ElevenLabs' JSON answer beside the recording. `README.md` is the user guide. The full
background, design decisions and history are in `docs/HANDOFF.md` (kept out of git); read it before
larger changes. It has not been brought up to date with the review fix round yet, section 6.4 above
all, so where it disagrees with the code or with this file, the code wins.

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
  `WasapiLoopbackAudioCapture.CaptureLoop`, `WasapiLoopbackCaptureProvider.GetDefaultDeviceId`,
  `TranscriptionWorkflow.TryPrepare` and the retry catch in `ElevenLabsClient.TranscribeAsync`. Use
  the phrase for every new one and hold it to the same standard. `docs/HANDOFF.md` section 6.4 lists
  them too, but is out of date until it is refreshed; until then the comments are the list.
- Time every interval on the steady clock (`TimeProvider.GetTimestamp` and `GetElapsedTime`), never
  the time of day, so a change of the Windows clock neither stretches nor shortens it. In tests,
  `ManualTimeProvider.Advance` moves both clocks and `StepWallClock` only the time of day.
- TDD with NUnit and Moq: write the failing test first. Run `dotnet test` before handing code over.
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
dotnet test --filter TestCategory=AudioDevice                          # Explicit tests on real devices
dotnet run --project src/ClassScribe                                   # the menu
dotnet run --project src/ClassScribe -- record "Kalam"                 # record; Q twice stops and transcribes
dotnet run --project src/ClassScribe -- record-only "Kalam"            # record only
dotnet run --project src/ClassScribe -- transcribe "C:\path\to\folder-or-file"
dotnet publish src/ClassScribe -c Release -r win-x64 --self-contained false -o publish
```

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
  process tree through Windows process loopback (`ProcessLoopback`). Menu choice 6 changes it per run.
- Exit codes: 0 success; 1 failure, settings that fail the check included; 2 unknown command;
  130 cancelled.
- Recordings go to `Recording:OutputFolder` (`%USERPROFILE%\ClassScribe`; a relative path is taken
  from the user profile, see `ClassFolders.Root`): a folder per recording name, and files named
  `<name>_<yyyy-MM-dd_HH-mm-ss>Z` (UTC start) with `.wav`, `_partN.wav`, `.marks.txt`, `.md` (the
  Markdown transcript), `.json` (ElevenLabs' answer), `.upload.wav` (the 16 kHz copy, removed once
  the transcript is saved unless `Recording:KeepUploadFile`) and `.unread.json` (an answer ElevenLabs
  charged for that did not become a transcript; a `.json` left without its `.md` counts as one too,
  see `RecordingFiles.FindKeptAnswers`; menu choice 3 shows `answer kept` and asks before a charged
  retry). `RecordingFiles` owns the naming; a transcript is never overwritten (`(2).md`,
  `(3).md`), and `HasTranscript` counts any copy.
- Settings are in `src/ClassScribe/appsettings.json`, read once at start from the exe's folder
  (`AppHost` turns the host's reload on change off), and checked by `SettingsCheck` before `App`
  runs: a value it cannot use stops the app with exit code 1 and a message that names the setting.
  A new setting needs its check there and a row in the README. Environment overrides such as
  `ElevenLabs__BaseUrl` work.

## Layout

- `src/ClassScribe`: `Program.cs` (the console set to UTF-16, a start-up failure printed plainly,
  the close prompt), `AppHost` (builds the host: settings read once, the key from the environment,
  the plain log format; then runs `SettingsCheck` before `App`), `App` (the command line),
  `ServiceCollectionExtensions` (the registrations), `Durations`, `PlainConsoleFormatter`.
- `src/ClassScribe/Settings`: the options classes, `ApiKeyFromEnvironment`, and `SettingsCheck`
  (types, ranges and paths, checked at start).
- `src/ClassScribe/Audio`: capture (`Windows/` holds the WASAPI code), `RecordingSession`, WAV
  writing (each packet goes to Windows at once; the header is rewritten and flushed to disk every
  `Recording:FlushSeconds`), upload preparation.
- `src/ClassScribe/Transcription`: ElevenLabs client (a Refit interface plus `ElevenLabsClient`
  around it), glossary loader, transcript formatter, workflow.
- `src/ClassScribe/Storage`: class folders under the output folder, recording file names, and marks.
- `src/ClassScribe/Commands`: the menu, the `record` and `transcribe` commands, the console and
  desktop abstractions, the close prompt and the exit warnings. Each command runs in its own DI
  scope (`ICommandRunner`).
- `tests/ClassScribe.Tests`: NUnit and Moq; hand-written fakes for the device, keyboard, clock and
  HTTP transport are in `TestDoubles.cs`; `ClassScribe.Tests.runsettings` holds the hang guard.
