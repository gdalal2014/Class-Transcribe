# ClassScribe

A .NET 10 console app for personal study. During an online class it records whatever the laptop
plays, by WASAPI loopback: the output device the class plays on, a device pinned by name, or one
app's sound through process loopback. It does not record the microphone yet. The recording runs in
a separate recorder process (`ClassScribe.exe --recorder <run id>`), which the window shows through
a named pipe, so closing the window does not end a class. When stopped, it sends the recording to
ElevenLabs Scribe v2 with a glossary of Arabic terms and saves a Markdown transcript and
ElevenLabs' JSON answer beside the recording. `README.md` is the user guide. The full background,
design decisions and history are in `docs/HANDOFF.md` (kept out of git); read it before larger
changes. It has not been brought up to date with the review fix round, with increment 1 (settings
read before each run, rebuild and the transcribe guards, the growing upload limit, the arrival
watch, keep-awake, the device check's COM fix) or with increment 2 (the process split, the command
line, the alerts, continue and the report at the start) yet, section 6.4 above all, so where it
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
  `StorageServices`, `AudioServices`, `TranscriptionServices`, `DesktopServices`, `CommandServices`,
  and last `Commands/WindowServices.cs`, the window's side of a run in the background, which
  replaces the stand-in `NoStartupReport`), called from `ServiceCollectionExtensions.AddClassScribe`;
  `AppHost` adds the settings reload (`SettingsServices.AddSettingsReload`), since it builds the
  configuration the reload works on. `AppHost.Build` validates scopes and every registration
  (`ValidateScopes`, `ValidateOnBuild`), so a per-run service taken outside a run's scope, or one
  that cannot be made, fails at once. `AppHost.BuildRecorder(start, apiKey, log, folder, time,
  configureServices)` builds the recorder's host from the same registrations plus `AddRecorder`
  (the link's console, no window signals, `LinkStatusSink`, `RecorderResultShower`, the server, the
  alerts and the process starter in place of the window's), from the request's settings snapshot and
  the key alone: no appsettings.json, no environment source, no console logger, scopes validated but
  not the whole container. There is no `Link/LinkServices.cs`: the view makes its own client, the
  server is the recorder host's.
- Every start of a recording is a `RecordRequest` with a `StartOrigin` (`Commands/Requests.cs`),
  through `ICommandRunner.RecordAsync` to `IRecordStarter` in a scope of its own; `ICommandRunner`
  no longer has `RecordAsync(string, bool)`. Run ids are made only by `RunIds.New` and checked with
  `RunIds.IsWellFormed` (`ProcessArguments.IsRunId` is the same rule) before they name a file, a
  pipe, an event or a command line. Run and request files go through `RecorderJson`, which writes
  enums by name, names each class source by `kind`, and passes over unknown fields; a field added to
  `RunRecord`, `RecorderState` or `RunOutcome`, which also travel on the link, needs a default, or
  older windows cannot read it, and anything else needs a new `LinkProtocol.Version`.
- The link (`Link/`): one named pipe per run, `ClassScribe-<run id>` (`LinkProtocol.PipeNameFor`),
  `CurrentUserOnly`, the first instance, up to 4 windows, lines of UTF-8 JSON of at most 64 KB; the
  server never logs, waits or raises events inside its `RecorderLog.Written` handler, and drops a
  stalled window on the thread pool. `RecorderLog` takes no `ILogger` (the provider writes to it,
  which would be a cycle); followers of `Written` are called in the background in `Seq` order and
  must not log, block or throw.
- No sound, ever: never write BEL anywhere; terminal sequences end with ST (`ESC \`). The session
  window (`SessionWindow`) keeps its title `ClassScribe`, which Windows' shutdown screen shows;
  `ShutdownBlockReasonCreate`, `Destroy` and `Query` run only on the window's own thread.
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
  the files stay under a megabyte. Every test that builds the app with `AppHost.Build` and runs a
  command or the menu must register a `TestRuntime`'s `RuntimeFolder`
  (`AddSingleton(runtime.Folder)`): the start-up report, the runner and the starter read and mark
  the run files, and the real `%LOCALAPPDATA%\ClassScribe` may hold a class in progress. The
  recorder's seams for tests are `RecorderHost { Folder, Time, ConfigureServices }` and
  `AppHost.BuildRecorder(..., configureServices)`. Never set the real console title, write a
  sequence to the real console, flash a visible window, or read the console in a recorder. Two
  NUnit pitfalls: `Does.Contain`, `Does.StartWith` and `Does.EndWith` compare with the current
  culture, where ICU ignores control characters, so check those ordinally
  (`text.Contains('\a')`, `EndsWith(..., StringComparison.Ordinal)`); and a `[TestCase]` whose
  arguments hold a control character gets a name VSTest cannot match, so under a `--filter` it
  silently never runs. A burst of log lines larger than `RecorderServer.QueueCapacity` (2,000) drops
  even a window that is reading, so tests write in realistic chunks.
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
dotnet test --filter TestCategory=WindowsProcess                       # real processes, pipes, windows, all of the test's own
dotnet run --project src/ClassScribe                                   # the menu (a recording then runs in the window)
dotnet run --project src/ClassScribe -- record "Kalam" --device auto --output "<folder>"       # record; Q twice stops and transcribes
dotnet run --project src/ClassScribe -- record-only "Kalam" --app chrome --output "<folder>"   # record only
dotnet run --project src/ClassScribe -- transcribe "C:\path\to\folder-or-file"   # --no-open opens nothing; --output for a file
dotnet run --project src/ClassScribe -- transcribe "<path>" --again    # one with a transcript; billed again
dotnet run --project src/ClassScribe -- rebuild "C:\path\to\folder-or-file"      # free, from the saved .json; --no-open
dotnet run --project src/ClassScribe -- list                           # the recordings, newest first; --output
dotnet run --project src/ClassScribe -- devices                        # the devices and apps with sound
dotnet run --project src/ClassScribe -- record --help                  # help per command; --help, -h, -?, /?, help <command>
dotnet publish src/ClassScribe -c Release -r win-x64 --self-contained false -o "$env:LOCALAPPDATA\Programs\ClassScribe"
```

- Classes run from the published copy (never single-file), with a Bitdefender ATD exception for that
  exe (README, Set up once): a recording then runs in a separate recorder process
  (`ClassScribe.exe --recorder <run id>`, under dotnet `dotnet ClassScribe.dll --recorder <run id>`;
  `RecorderLauncher` starts it through `IProcessStarter` with `CreateProcessW`: no window, its own
  process group, no inherited handles, breakaway where the job allows it, working folder
  `%LOCALAPPDATA%\ClassScribe`, the environment inherited as it is), which closing the window does
  not stop; `Program` hands `--recorder` to `RecorderHost` before anything touches the console. The
  recorder never reads appsettings.json: the window hands over `SettingsReloader.Snapshot()` in the
  request file, and the key comes from the recorder's own environment, so a stub
  `ElevenLabs__BaseUrl` reaches a recorder only through the window's snapshot. The window shows the
  run through a named pipe (`Link/`), and `attach --alert <run id>` is the minimized window the
  recorder brings back by itself (`UnattendedAlerts`): 3 s after the last window closed while
  recording (not after Esc), at once for an Alert or a failure, after 2 minutes of a Notice, one at
  a time, at most one every 2 minutes, snoozed for closes after a close within 2 minutes; its keys
  do nothing until Enter, and it never opens results or marks a run seen. Under `dotnet run` the
  job kills on close (`JobInspector`), so the run records in the window and says so; so it does
  with `Recording:SeparateProcess` false, when `%LOCALAPPDATA%\ClassScribe` cannot be written, and
  after two failed starts of the recorder. The recorder's own exit codes are internal: 0 done, 1
  failed, 3 refused by the lock, 4 no request or one it cannot take, 130 cancelled; the window turns
  them into the codes below. Its lines go to `%LOCALAPPDATA%\ClassScribe\recorder.log`
  (`recorder.old.log` at 1 MB) until the recording's folder is known, then into `<base>.log`.
- `record` and `record-only` need the name, a source (`--device "<part>"`, `--device auto` or
  `--app <process>`) and `--output`; one validator names everything missing, shows that command's
  help and exits 2, and nothing is prompted (`CommandLineTree` on System.CommandLine 2.0.13: `Tidy`,
  `Parse` with ClassScribe's own errors and exit 2, `KindOf`, `InvokeAsync` with Ctrl+C left to
  ClassScribe; `App` is thin: parse, then the gate and the start-up report for commands, then
  invoke). `--continue` / `--new` decide about a recent recording of the same name without a
  question; the menu asks C, N or Esc (`ContinueQuestion`). `transcribe <path> --output` is passed
  on as a full path for a file that is not a recording's. `list` and `devices` work;
  `attach` is hidden. Commands printed for later (`CommandLine`) name the program by its full path
  and escape PowerShell's `$`, backtick and double quotes.
- One recording at a time: `%LOCALAPPDATA%\ClassScribe\recording.lock` (`RecordingLock`,
  `FileShare.None`, held only while capturing, so the next class may start during an upload). Run
  files in `runs\<run id>.json` (`RunJournal`, `RunRecord`: state, a heartbeat every 10 s, `EndedBy`,
  the outcome, `Seen`; written atomically, retried three times 100 ms apart; seen runs pruned after
  30 days) are the C5 marker and the next start's report (`StartupReport`: C through
  `ICommandRunner.RecordAsync` with `StartOrigin.StartupReport`, T, O and F carried out by the
  report itself). `StopSignal` is the per-run event `Local\ClassScribe.Stop.<run id>`, the one stop
  that needs no pipe. A recorder lost while a window shows it is continued by the starter
  (`RecordStarter`: a new recorder, then in the window after a second loss within 10 minutes;
  declined from an alert window, `StartOrigin.AutoContinueFromAlert`).

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
  (`WindowsPowerRequestsTests`, `WindowsSystemTimesTests`). `WindowsProcess` reaches Windows for the
  process split, with the test's own things only: `ProcessStarterTests` and `ProcessLivenessTests`
  start one hidden `cmd.exe` that ends at once (never ClassScribe); `JobInspectorTests` read the
  test process's job; `NamedPipeLinkTests` and `StopSignalTests` use real pipes and events named
  after fresh run ids; `RecordingLockTests` hold a real lock file in a temporary folder;
  `SessionWindowTests` make a real hidden window and send session messages to that window alone,
  never broadcast; `ConsoleWindowSignalsTests` read the real process tree and console facts;
  `RecorderHostTests` run the real host over a temporary ClassScribe folder, with a real pipe named
  after a fresh run id that nothing connects to, the real stop event, a `FakeRecording`, a
  `FakeProcessStarter` (no process is ever started), `FakeSessionEvents` and `FakePowerRequests`,
  ElevenLabs and the desktop mocked, and the windows as `TestWindow`s over in-memory connections to
  the recorder's real server, captured through `RecorderHost.ConfigureServices`; `EndToEndTests`
  run the window and the real `RecorderHost` in one process over one `TestRuntime` folder, the view
  over `DuplexStreamPair` connections, with `FakeCaptureProvider`, `StubHttpHandler` and the dummy
  key, on the real clock. `ServiceRegistrationTests` cover both roles (the recorder's through
  `AppHost.BuildRecorder`) and run `list`, `transcribe` and `rebuild` through the real `App`; the
  `devices` registration test is `WindowsAudio`, since it lists the real devices once. Nothing is
  played or recorded. The suite had 887 tests at the end of increment 1 and 2,134 at the end of
  increment 2.
- The hang guard: `tests/ClassScribe.Tests/ClassScribe.Tests.runsettings` sets `TestSessionTimeout`
  to 10 minutes, and the test csproj names it in `RunSettingsFilePath`, so a plain `dotnet test`
  aborts a run that deadlocks and fails it instead of hanging. The suite normally takes well under a
  minute. `[CancelAfter]` cannot interrupt a blocked `Wait`, so it is no guard on its own.
- The two `AudioDevice` tests in `WindowsCaptureTests` are `[Explicit]`: they open the laptop's real
  output devices (listening only), so run them only when the user is not in a class or an audio test.
- Publish as a folder, as above, never single-file: Bitdefender quarantined single-file builds
  (`-p:PublishSingleFile=true`) as Gen:Variant.Yogi.67941, a false positive, and the README no
  longer offers them.
- While recording: Q twice within 5 s stops (the first Q logs "Press Q again within 5 seconds to
  stop"); Ctrl+C twice within 5 s stops the same way, and after the stop one Ctrl+C cancels the
  preparation and upload (with a 2 s allowance after a stop by Ctrl+C); P pauses or resumes; M
  writes a mark to the recording's `.marks.txt`; Esc leaves the view of a background recording
  without stopping it (from the menu, the menu comes back and offers A). A window the recorder
  opened by itself ignores every key until Enter.
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
- Exit codes: 0 success, and Esc leaving a recording that carries on; 1 failure, settings that fail
  the check included, a refused command (a transcribed recording without `--again`, a recording
  still being written, `rebuild` without a saved answer), another recording running, and a lost
  recorder that was not continued; 2 a mistake on the command line, named with that command's help
  (a missing name, source or `--output`, two sources, a bad value, an unknown word, a path that is
  not there); 130 cancelled. A window closed during a background recording gives none; the run file
  in `%LOCALAPPDATA%\ClassScribe\runs` keeps the outcome for the next start's report (`StartupReport`).
- Recordings go to `Recording:OutputFolder` (`%USERPROFILE%\ClassScribe`; a relative path is taken
  from the user profile, see `ClassFolders.Root`): a folder per recording name, and files named
  `<name>_<yyyy-MM-dd_HH-mm-ss>Z` (UTC start) with `.wav`, `_partN.wav`, `.marks.txt` (marks, and
  notes written as `hh:mm:ss.fff ~ words`, such as the resume note `ClassMarks.AddResume` writes and
  the sleep notes; `ClassMarks.PartsOf` counts the parts), `.log` (everything the recording's runs
  logged, in both homes, `RecorderLog.WriteFileTo`), `.md` (the
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
  key), Transcript and Logging sections as they are now, flattened; `RecorderLauncher` hands it to
  the recorder in the request file, and `RunJournal.WriteRequest` leaves the key out should it ever
  be in it. A recording that is running keeps the settings it started with.

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
  `StayAwake`, all in `IPowerRequests.cs`), the computer's clocks (`ISystemTimes`), the window's
  title, taskbar and flash (`IWindowSignals`, `TitleText`, `WindowStatusSink`), starting processes
  (`IProcessStarter`), the job check (`IJobInspector`) and Windows' end of the session
  (`ISessionEvents`); `Desktop/Windows` holds the Windows calls. `DesktopServices` registers them
  and `FreezeWatch`.
- `src/ClassScribe/Recorder`: the separate recorder: `RecorderHost.cs` (`RecorderStart` and
  `RecorderHost`: the recorder process from the request to the end, with `IsRecorderStart` for
  `Program`), `RuntimeFolder` (`%LOCALAPPDATA%\ClassScribe`), `RecordingLock`, `RunJournal` with
  `RunView` and `RunHandle` (run and request files, `MarkSeen`, `MarkSeenIfOver`, prune),
  `RunRecord.cs` (run file, request file, `RunEndCause`), `RecorderJson`, `ProcessLiveness`,
  `StopSignal`, `RecorderLog.cs` (`LogEntry` and `RecorderLog`: the ring of 20,000 lines, `Written`,
  the files) and `RecorderLogProvider`, `LinkConsoleUi` (the windows' keys as the recorder's
  console), `LinkStatusSink` (states to the windows and the alerts, repeated once a second),
  `RecorderResultShower`, `UnattendedAlerts` (the minimized window brought back by itself; a window
  that left with Esc counts as no close) and `FreezeWatch`.
- `src/ClassScribe/Link`: one named pipe per run (`LinkProtocol`, `LinkTypes` and `LinkKeys` in
  `LinkMessages.cs`, `LineChannel`, `RecorderServer`, `RecorderClient`); messages are lines of JSON
  (`LinkJson`), and a field added to a record that travels on it needs a default.
- `src/ClassScribe/Audio`: capture (`Windows/` holds the WASAPI code), `RecordingSession`, WAV
  writing (each packet goes to Windows at once; the header is rewritten and flushed to disk every
  `Recording:FlushSeconds`), upload preparation; `RecordingMode`, `Track`, `IRecording` (the
  recording a run drives, and its factory), `ICaptureSource` and `LoopbackSource` (what the computer
  plays, through `CaptureChooser`, which follows the run's `ClassSource`), `ClassRecording` with
  `ClassRecordingFactory` (an `IRecording` over one session of the class alone, from a run's
  `SourcePlan`), `SourceChoice` (the menu's pick, or the settings, as a `SourcePlan`).
  `AudioServices.cs` holds `LazyLoopbackCaptureProvider`: `ILoopbackCaptureProvider` is made on first
  use, so the app, `transcribe`, `rebuild` and `list` never touch Windows audio, and
  `Lazy<ILoopbackCaptureProvider>` (for `devices`) resolves the registered provider.
  `Audio/Windows/CoreAudioInterop.cs` holds
  ClassScribe's own Core Audio declarations and `CoreAudio` (`CreateEnumerator`, `Check`, `Release`):
  the device check lists through them, and NAudio does the notifications, the device names and the
  captures.
- `src/ClassScribe/Transcription`: ElevenLabs client (a Refit interface plus `ElevenLabsClient`
  around it), the shared answer parser (`ElevenLabsAnswer`), the upload's time limit
  (`UploadTimeLimit`), glossary loader, transcript formatter, workflow (rebuild included), and
  `TranscriptionProgress` (scoped: the workflow and the client report preparing, uploading at a
  percent and waiting; `RecordCommand` shows it as UPLOADING 40% and writes it into the run file's
  heartbeat, which the menu header and `list` read).
- `src/ClassScribe/Storage`: class folders under the output folder, recording file names, numbered
  transcript copies (`TranscriptCopies`, never overwritten), and marks.
- `src/ClassScribe/Commands`: the menu, the command line (`CommandLineTree` on System.CommandLine,
  `App`, `CommandLine` for the commands printed for later), the contracts (`Requests.cs`:
  `RecordRequest`, `SourcePlan`, `ClassSource`, `ContinueChoice`, `StartOrigin`, `RunIds`;
  `StarterContracts.cs`: `ViewMode`, `ViewEnd`, `IRunViewer`, `IRecordStarter`, `IRecordRun`,
  `IStartupReport`, `IResultShower`; `RecorderState.cs`), the `record` run (`RecordCommand` as the
  `IRecordRun` in either home, `RecordingLoop` for the keys, states and heartbeat, `RecordingMonitor`
  for the once-a-second checks, `RunBookkeeping.cs` for the lock, the run file, the recording's log,
  keep-awake and the end handlers, with `RunContext`, `RunContextSlot` and `ICrashWarnings`,
  `WindowResultShower`), the starter (`RecordStarter`, `RecorderLauncher`, `ContinueQuestion`), the
  view of a background run (`RecorderView`, `AttachCommand`), the report at the start
  (`StartupReport`), `transcribe`, `rebuild`, `list` and `devices`, the gate (`RunGate`), the
  console and desktop abstractions, the name prompt, the close prompt and the exit warnings, and
  `WindowServices.cs` (the registration of the window's side). Each command runs in its own DI scope
  (`ICommandRunner`: record through `IRecordStarter`, attach through `AttachCommand`, list and
  devices through their commands; `TranscribeAsync` refuses a recording a live run records or
  transcribes). A start from the window the recorder opened by itself is
  `StartOrigin.AutoContinueFromAlert`: shown with the keys locked, never recorded in that window,
  never marked seen.
- `src/ClassScribe/Desktop/Windows`: `ProcessStarter` (`CreateProcessW` behind `IProcessCreationApi`),
  `JobInspector` (`IsProcessInJob` and `QueryInformationJobObject` behind `IJobApi`),
  `SessionWindow` (with `IShutdownReasonApi`), `ConsoleWindowSignals` (`IConsoleWindowApi`:
  `WindowsTerminalVariableSet`, `ClassOf`, `HasTaskbarButton`), `TaskbarList` (`ITaskbarList3`
  declared with `ITaskbarList`'s and `ITaskbarList2`'s methods first, since COM interop ignores
  interface inheritance), `WindowFinder` (`NativeProcessTree`), `WindowsPowerRequests` and
  `WindowsSystemTimes`.
- `tests/ClassScribe.Tests`: NUnit and Moq. Hand-written fakes for the device, keyboard, clock and
  HTTP transport are in `TestDoubles.cs`, with `TestOptions<T>`, `TestSettings`,
  `FakeAudioCapture.DeliversContinuously` (default false, like device loopback), and
  `FakeCaptureProvider.SetAppLevel` and `FailListings`. Others are in `TestDoubles.*.cs`:
  `TestDoubles.CoreAudio.cs` (fakes of Windows' enumerator, devices and sessions, and
  `FakeComLedger`, which checks that every object handed out is released once),
  `TestDoubles.Power.cs` (`FakeSystemTimes`, `FakePowerRequests`, `FakePowerSource`,
  `FakePowerRequestApi`), `TestDoubles.Commands.cs` (`ZonedTimeProvider`),
  `TestDoubles.Recording.cs` (`FakeRecording`, `FakeRecordingFactory`), `TestDoubles.Runs.cs`
  (`FakeProcessLiveness`, `TestRuns`, `TestRuntime`: lock and run files in a temp folder, so tests
  never touch the real `%LOCALAPPDATA%\ClassScribe`), `TestDoubles.Process.cs` (`FakeProcessStarter`,
  `FakeLaunch`, `FakeJobInspector` with `DotnetRun`, `KillOnCloseWithBreakaway` and
  `BreakawayWithoutKill`, `FakeJobApi`, `FakeProcessCreationApi`, `FakeCreateProcess`),
  `TestDoubles.Session.cs` (`FakeSessionEvents`, `FakeShutdownReasonApi`), `TestDoubles.Signals.cs`
  (`FakeWindowSignals`, `FakeConsoleWindowApi`, `FakeTaskbarProgress`, `FakeProcessTree`,
  `FakeTaskbarList3`), `TestDoubles.Link.cs` (`DuplexStreamPair`, an in-memory pipe; `TestWindow`,
  a window speaking the protocol by hand to a `RecorderServer`; `TestRecorder`, a real server over
  in-memory connections; `RecorderByHand`; `ClockSteps`), `TestDoubles.RecordRun.cs`
  (`FakeCrashWarnings`, `WatchedRecordingFactory`, `StalledStream`, `FakeResultShower`),
  `TestDoubles.Starter.cs` (`PlayedRecorder`, `FakeRunViewer`, `FakeRecordRuns`),
  `TestDoubles.Menu.cs` (`PlayedRuns`, `FakeRecordStarter`, `WatchedScopes`),
  `TestDoubles.View.cs` (`FakeCtrlC`, `FakeStopRequests`) and `TestDoubles.CommandLine.cs`.
  `Denied.cs` holds ACL helpers; `ClassScribe.Tests.runsettings` holds the hang guard. The test
  project sees ClassScribe's internals (`InternalsVisibleTo` in `CoreAudioInterop.cs`).
