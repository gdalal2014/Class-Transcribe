# ClassScribe

A .NET 10 console app for personal study, built as four projects under `src`: `ClassScribe.Core` (the records and
rules), `ClassScribe.Application` (the use cases and the ports), `ClassScribe.Infrastructure` (Windows, the vendors and the
files) and `ClassScribe.Cli` (the menu, the command line and the hosts, whose exe is still `ClassScribe.exe`), each using
only those named before it (see the rule of dependencies under Conventions, and Layout). During an online class it records
whatever the laptop plays, by WASAPI loopback: the output device the class plays on, a device pinned by name, or one
app's sound through process loopback. It can record the user's microphone beside the class, as a
second track on the same clock, written only while the class app is listening on that microphone
and Windows is not muting it, and mixed into the class's track at upload; or the microphone alone,
for a class in a room, and then, for a lesson video, the camera's picture beside it, shared, into pieces on the same
clock, written by a camera process of its own, and joined after the stop with the sound into the lesson's finished video,
`<base>.mp4`, by another camera process's finish job. The recording runs in a separate recorder process
(`ClassScribe.exe --recorder <run id>`), which the window shows through a named pipe, so closing
the window does not end a class. When stopped, it sends the recording to ElevenLabs Scribe v2 with
the glossary decided as the recording started (none is shipped: the settings or a saved class name
one; Gulzar's is his own file in `%APPDATA%\ClassScribe`) and saves a Markdown transcript and ElevenLabs' JSON answer beside the
recording, in a course > date folder (`<root>\Mantiq\2026-10-17\Mantiq_2026-10-17_14-04-42Z.*`;
the old flat `<root>\<name>\` folders are still read). It signs in to the user's own Google Drive
(OAuth for a desktop app, `drive.file` only), uploads each class there by itself once its transcript
is saved (record-only: once its files are closed), from the recorder or the window that ran it, into
a tree that mirrors the laptop's (`My Drive > ClassScribe > Mantiq > 2026-10-17`); what could not go
up waits in a queue for the next class or `drive upload` / menu choice 9 > U; each file is checked
against Drive's MD5; and it frees room on the laptop by deleting audio Drive has (`drive free-space`,
9 > L), never touching Drive. It also takes YouTube videos (`youtube`, menu choice Y), a batch at a
time in the window, each made an ordinary recording of a course, all of a batch in one date folder,
with a `<base>.source.json`, then transcribed: by default recorded as it plays, attended, in a Chrome
window of ClassScribe's own (`--user-data-dir=<RuntimeFolder>\video-browser`, an `--app` window,
recorded through process loopback on its browser process, one run per video through the starter);
or, with `--download` (D), downloaded with yt-dlp, for videos Gulzar may download and never his
course's. `README.md` is the user guide. The full background, design decisions
and history are in `docs/HANDOFF.md` (kept out of git); read it before larger changes. It has not
been brought up to date with the review fix round, with increment 1 (settings read before each run,
rebuild and the transcribe guards, the growing upload limit, the arrival watch, keep-awake, the
device check's COM fix), with increment 2 (the process split, the command line, the alerts, continue
and the report at the start), with increment 3 (the microphone, the QPC timeline, the microphone
check), with the cost line and the Bluetooth warning, with the course and date folders, with
Google Drive (sign-in, uploads, the queue, free space), or with YouTube videos yet, section 6.4 above all, so where it
disagrees with the code or with this file, the code wins. Google Drive's design is `drive.md`
(Claude's design notes of 17 October 2026, outside the repo); its section 2.4 tree
(`ClassScribe > <recording name>`) was replaced by the course > date mirror above. YouTube's design is
`youtube.md` (Claude's design notes of 18 October 2026, outside the repo), built in two slices
(`youtube-download`, then `youtube-record`) with Gulzar's answers to its section 10, which override
it where they differ: the download path built as designed (1 A); attended playback (2 A); a course
video's link may be recorded as it plays, never downloaded, with no course-channel refusal on the
record path (3 B); one date folder per batch (4 A); a download keeps YouTube's own sample rate, mono
(5 B, about 318 MB an hour at 44.1 kHz; the upload copy is 16 kHz as always); `YouTube:DriveUpload`
`Everything` by default (6 B); `--allow-unlisted`, with the safeguard below (7 B); a download's
positions link to YouTube (8 A); his YouTube Premium sign-in lives in the video window's profile,
which is a secret (9 B); winget's stable yt-dlp, warned at 90 days, `--update-to nightly` as the
advice after a format error (10 A).

## Ground rules

- ClassScribe records only audio the user is playing: a class on its page, or a YouTube video he plays
  in ClassScribe's own Chrome window (`youtube` without `--download`), which ClassScribe opens when he
  asks and never plays by itself (no autoplay switch, no next video without his key). Never add
  anything that downloads from the course website, takes links or media out of its pages, or gets
  around its protection. The course's own videos, unlisted YouTube videos shown on the course page
  included, are only ever recorded as they play (from their YouTube link in ClassScribe's window, or
  on the course page with `record --app chrome`), never downloaded. The one download is
  `youtube --download`: yt-dlp on YouTube links Gulzar gives it, only for videos he uploaded or whose
  owner allows him to download them, as his informed choice (YouTube's terms allow downloading only
  where YouTube offers it, or with written permission from YouTube and the rights holders; the README
  says so). It is chosen each time and confirmed for each batch (`YES`, or `--yes` after the plan), and
  for each video that is not public: his word in the menu, its channel in `YouTube:MayDownloadChannels`,
  or `--allow-unlisted`, which takes only videos yt-dlp says are unlisted, of a channel its read names,
  is refused (exit 2) while `YouTube:CourseChannels` is empty, never overrides another refusal of the
  guard, and is kept in the video's `.source.json` as `"acceptedBy": "allow-unlisted"`. It never signs
  in and never gives yt-dlp cookies, a browser's data or credentials, for any profile (no `--cookies`
  or `--cookies-from-browser`; `YtDlpArguments.ProblemWith` refuses them and every other option of
  `Forbidden`, and `FakeToolRunner` checks every list a test builds); and it refuses embedded players'
  links, watch links copied from an embedded player's YouTube button, links that are not YouTube's,
  and the channels in `YouTube:CourseChannels` (while that names any, a video whose read names no
  channel needs his word). Keep it that way.
- The video window's profile, `<RuntimeFolder>\video-browser`, holds Gulzar's YouTube Premium sign-in
  (Google session cookies) once he signs in there: a secret. ClassScribe never reads, lists, copies,
  uploads, deletes or logs anything in it, and never makes it (Chrome does, on first use); it only
  names it to Chrome as `--user-data-dir`, keeps that command line out of every log (the browser's
  start logs the program and flags alone), and runs Chrome in `<RuntimeFolder>\youtube`, never in the
  profile; yt-dlp is never given it (`YtDlpArguments.ProblemWith` refuses any argument that names
  it); nothing of it goes to Google Drive or a `.log`; and no test ever makes a real one.
- A real video window plays YouTube on this laptop, and a real download reaches YouTube from his
  connection (and its transcript is billed): ask before either. Tests never start Chrome, yt-dlp,
  Deno or FFmpeg, never touch a real window, and never reach YouTube: `TestGuards` turns on
  `YouTubeGuards.RefuseTheRealThingsSwitch`, under which `ProcessToolRunner` made by DI refuses every
  tool (naming `FakeToolRunner`), `NativeProcessCreationApi`'s tool and browser path refuses any
  program but Windows' own `cmd.exe` (which `ProcessToolRunnerTests` start), `ChromeWindows` refuses
  `TitlesOf` and `Close` (naming `FakeVideoWindows`), and `YouTubeHostGuard` on the oEmbed client
  refuses YouTube's hosts (naming `StubHttpHandler` at a stub address). Manual dry runs can point
  `YouTube__OEmbedBaseUrl` and `ElevenLabs__BaseUrl` at a local stub and `YouTube:YtDlpPath` at a
  stand-in; Chrome cannot be stubbed, so a record path's dry run plays a real page.
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
- Google Drive credentials: the Google client (id and secret) is imported from the client file
  Google offers (Download JSON) and kept, with the refresh token, only in
  `%APPDATA%\ClassScribe\google-drive.dat`, DPAPI-protected for the Windows account
  (`DpapiGoogleCredentialStore`). Never print, log, commit or put in `appsettings.json`, a run or
  request file, a test or the recording's `.log`: the client secret, a refresh or access token, an
  authorization code, the PKCE verifier, the state, or an upload session's address (its
  `upload_id`; it is kept only in the recording's manifest under `%LOCALAPPDATA%\ClassScribe\drive`
  while that upload is unfinished, and `UploadSession`/`UploadAddress` print without it). The
  authorization address goes to the window only. Status shows only the end of the client id
  (`GoogleClient.ShortId`). `client_secret*.json` is in `.gitignore`. A real sign-in, a real upload
  (it sends the class to Google) and any real request to Google are the user's to make: never send
  anything to Google from a test or a manual run without asking; tests use `FakeGoogle`, and
  `Drive__ApiBaseUrl` and `Drive__OAuthBaseUrl` can point a dry run at a local stub. ClassScribe only
  creates on Drive: it never deletes, trashes, shares or overwrites anything there, with one
  exception Gulzar allowed: a new revision of its own copy of a text file that has grown (a log, the
  marks, an answer laid out anew; `RecordingUpload` sends a revision only when the text file is
  larger than Drive's copy, and to its copy that is still there outside the trash, or to repair its own
  copy whose MD5 check failed), and, as teach-video 8 asks and Gulzar chose on 29 October 2026 (the 0.5.0
  review's answer 4 (b)), a new revision of its own verified copy of a lesson's video (`<base>.mp4`) made again, larger
  or smaller, sent with Drive's `keepRevisionForever=true` (`UploadTarget.KeepRevisionForever`, set for that revision
  alone, through `DriveClient.StartRevisionAsync`), so that Drive keeps the older video for ever instead of purging it
  after 30 days as it does a text file's older revision (`FakeGoogle` counts them as `RevisionsKeptForever`, and
  `RecordingUploadTests` pins which revisions carry the parameter); never of a `.wav`. Deleting local files,
  by `drive free-space` or by hand, never touches Drive. A test run turns on the AppContext switches
  `RoamingFolder.RefuseTheRealFolderSwitch`, `RuntimeFolder.RefuseTheRealFolderSwitch` and
  `ClassFolders.RefuseTheRealFolderSwitch` (`TestGuards`,
  the assembly's `[SetUpFixture]`), so a test that asks for the real `%APPDATA%\ClassScribe`,
  `%LOCALAPPDATA%\ClassScribe` or `%USERPROFILE%\ClassScribe` fails at once: with Gulzar signed in, a class recorded through the real
  app would otherwise go up to his own Drive after it ends, the real run files and Drive queue are
  his, and so are the classes in the recordings folder. Every test that builds the app or the recorder's host registers `new RoamingFolder(<temporary
  folder>)` or a `FakeCredentialStore`, and a `TestRuntime`'s `RuntimeFolder` (`RecorderHost.Folder` is
  made only when first asked for, so giving one is enough; `RuntimeFolder.RealRoot()` works out the
  real path, without the switch, for the one test of where it is).
- Do not turn on trimming or Native AOT: NAudio reaches Windows audio through COM interop.
- No test opens a capture stream on any microphone, and nothing plays a sound: the normal tests use
  the fakes, and the only tests that open a real device (listening only, the two `AudioDevice` tests
  in `WindowsCaptureTests`) are `[Explicit]` and are not run with the suite. The readers that look at
  the microphones (`WasapiMicrophoneProvider`, `WasapiMicrophoneSessions`, the usage record) never
  ask Windows for an audio client, the object through which a program opens a microphone, so they
  cannot open one; the fake endpoints fail a test that asks for one. Keep it that way: the menu, the
  `devices` command, the microphone check and the watch on the class app all rest on it. No test opens a
  camera either. `DeviceGuards` (`Desktop/DeviceGuards.cs`, the switch `ClassScribe.Devices.RefuseTheRealDevices`,
  turned on by `TestGuards` beside the others) makes the real start of a camera process and Windows' activation of a
  capture endpoint (`WindowsAudioClientActivator`, which reads the endpoint's direction through `IMMEndpointRaw` only
  while the switch is on) refuse in a test, naming the fake (`FakeCameraProcess`, `FakeAudioClientActivator`); an output
  device, which `AudioClient2SlotTests` stands in for a microphone, passes. `DeviceGuardsTests` pins the activator's
  guard over a fake enumerator (its internal constructor takes the enumerator's maker; `FakeCaptureEndpoint` answers
  `IMMEndpointRaw` with its `DataFlow`): a capture endpoint is refused before anything is asked of it. An `[Explicit]` test that must open a real
  device turns the switch off for itself and on again in a `finally`. Listing cameras (WinRT's device lists) opens
  nothing, so no light comes on. The camera's real open, `WindowsCameraCapture.InitializeShared` (MediaCapture's
  InitializeAsync), refuses in a test too, naming `FakeCameraCapture`, before anything is asked of Windows; the one real
  MediaCapture a test makes (`WindowsCameraCaptureTests`, WindowsCamera) is never initialised, which touches no device
  (the probe fix's rule 6: it checks that Windows still refuses before InitializeAsync what the fake refuses). The lesson's
  pieces are written by Media Foundation's sink writer, which opens no device: `MediaFoundationPieceWriterTests`
  (WindowsMedia, run with the suite) encode generated frames with the software encoder into a temporary folder and read
  them back. Every other test of the picture uses `FakePieceWriter` and `FakeCameraCapture`. The lesson's finished video
  is made by Media Foundation too, opening no device: `LessonVideoMediaTests` (WindowsMedia) join pieces the real piece
  writer made, with a `.wav` beside them, in a temporary folder; every other test of it plays the media work with
  `FakeLessonVideoMedia` (`TestDoubles.LessonVideo.cs`).
- Tests never read or write the real `%LOCALAPPDATA%\ClassScribe`, `%APPDATA%\ClassScribe` or
  `%USERPROFILE%\ClassScribe`: temporary folders only (`TestRuntime`, `TempFolder`). The real
  `%LOCALAPPDATA%\ClassScribe` holds the run files and logs of real classes now, and
  `%USERPROFILE%\ClassScribe` Gulzar's real class with his PDFs and notes beside it, so list both
  folders' files (names, sizes and times) before and after a full run and compare the lists, rather
  than expecting them empty; a test that wrote there is a bug. Leave the inside of `video-browser` out
  of the listing once it exists (it holds his Google sign-in), and check that no `youtube` or
  `video-browser` folder appeared there during the run. `%APPDATA%\ClassScribe\appsettings.json` is
  Gulzar's own settings file once he has made a change: list `%APPDATA%\ClassScribe` too, and never
  change his settings by hand or from a manual run; such a run sets `CLASSSCRIBE_SETTINGS_FOLDER` to
  a temporary folder first (the menu then names it), and tests use `SettingsFileFixtures`. The
  recordings folder has its guard too, since `--output` is optional: `ClassFolders.RefuseTheRealFolderSwitch`
  (on in `TestGuards`) makes `ClassFolders.RootFor` and `Root`, through which everything that reads
  or writes under the root resolves it, refuse the real `%USERPROFILE%\ClassScribe` and any folder in
  it, naming what to give instead; `ClassFolders.RootNamedBy` only names the folder, for the settings'
  warnings (`SettingsCheck.FolderWarnings`), which read and write nothing there;
  `SettingsFileFixtures.ShippedJson` names a placeholder (`PlaceholderOutputFolder`), never the real
  folder; and `RecordingsFolderGuardTests` (in `TestGuards.cs`) pins the guard. A test that shows the
  menu or runs a command without `--output` gives its settings a `Recording:OutputFolder` in a
  temporary folder.

## Conventions

- .NET 10 and C# 14, nullable on, file-scoped namespaces, primary constructors, records and
  `init` for immutable data, target-typed `new`, collection expressions, LINQ where it reads
  well, async/await, the built-in DI container. `ClassScribe.Core` and `ClassScribe.Application` are plain `net10.0`
  (architecture 1.2): they hold nothing of Windows, which every build proves, and a Windows-only call added to them
  without an `OperatingSystem.IsWindows()` guard gives CA1416, which the 0-warnings rule turns into a stop.
  `ClassScribe.Infrastructure`, `ClassScribe.Cli` and the test project target `net10.0-windows10.0.19041.0`
  (teach-video 6.3; Windows 10 version 2004 or later), for Windows' camera API (WinRT: the device lists and
  `MediaCapture`, both in 0.5.0, which holds the camera's steps 12, 1 and 13); a plain `net10.0` project cannot reference a
  Windows one, since NuGet refuses the reference (NU1201), which is why the Cli and the tests follow Infrastructure and not
  the other way round. NAudio still takes its
  `netstandard2.0` library, and CsWinRT's `ComWrappers` sits beside NAudio's and ClassScribe's built-in COM. The
  published folder grew from about 7 to 33 MB (`Microsoft.Windows.SDK.NET.dll` is 25 MB); trimming and Native AOT stay off.
  In a namespace that ends in `Windows` (`ClassScribe.Infrastructure.Audio.Windows`, `ClassScribe.Infrastructure.Desktop.Windows`
  and Infrastructure's `Camera.Windows` and `Drive.Windows`), WinRT's own namespaces need `global::Windows`.
- The rule of dependencies (architecture 2): `ClassScribe.Cli -> ClassScribe.Infrastructure -> ClassScribe.Application ->
  ClassScribe.Core`, each project using only the ones to its right. Core is ClassScribe's own domain (architecture 1.1): a
  record or rule it would have whichever vendor or operating system it ran on (the timeline; the audio packets, tracks,
  modes and snapshots; the microphone's records, looks and the gate's verdicts; the camera's frames, pieces and the camera
  process's messages; the recording, run and request records, file naming and the WAV header; the transcript records and
  the keyterm rules; the saved classes' records; the words for durations and sizes), with no I/O, no logging, no services
  and no vendor's records, and `TimeProvider` as the one port it shares with everything. Application holds the use cases,
  the services they drive, the options and the catalog, the ports and the records the ports exchange, and uses .NET's own
  abstractions as they are: `ILogger<T>`, `IOptionsSnapshot<T>`, `IOptionsMonitor<T>` and `IServiceScopeFactory`
  (architecture question 5: `RecordStarter` and `ScopedVideoTranscriber` open a run's scope with it). Infrastructure
  implements the ports with Windows, files, processes, pipes and web APIs, and holds the vendors' records and protocols,
  the JSON contexts and Refit's generated clients, and the helpers only adapters use (`WindowsCommandLine`, `ToolSearch`,
  `ChildEnvironment`, `DeviceGuards`), `internal` where nothing outside needs them. Cli talks to the person and composes
  the rest: it may use Infrastructure directly, as the composition root must; its menus and commands go through
  Application's use cases and ports. A port (architecture 4.1) is an interface in Application, in the folder of the area
  whose use cases need it, named `I...`; its adapter sits in Infrastructure under the same area name, or in Cli for the few
  that talk to the person (`IConsoleUi`, `IUserSignals`, `IContinuePrompt`, `ICameraPrompt`, `IRecorderStatusSink`,
  `IRunStarter`); what it takes and gives are Core records, records declared beside it in Application when they exist only
  for it (`DriveItem`, `VideoInfo`, `StartedProcess`, `AccountReading`) or .NET's own types (`Stream`, `TimeSpan`,
  `CancellationToken`), never an Infrastructure type; where a use case only passed a vendor's answer along, an Application
  record replaced the vendor's DTO and the adapter maps it, and a static helper that read the disk became a member of a port.
  The project references enforce the order (a backward reference closes a cycle and restore stops with MSB4006; a plain
  project referencing a Windows one is refused with NU1201), and the architecture test in
  `tests/ClassScribe.Tests/Architecture` (architecture 2.4; question 7: no analyzer package) checks what the build cannot.
  `ArchitectureFacts` reads the four built assemblies from the test's output folder with `System.Reflection.Metadata` (the
  assembly references, the type and member references, each method's IL and locals, and the type each catch clause names,
  which sits in the method's exception regions alone; `CatchClausePlant` in the test project pins that the reader sees
  it), and the pure `ArchitectureRules.Check` judges
  seven checks, each with its test in `ArchitectureTests` and planted cases in `ArchitectureRulesTests`: (1) the
  references, Core the framework alone, Application Core and `Microsoft.Extensions.Logging.Abstractions`, `Options`,
  `DependencyInjection.Abstractions` and `Primitives` (`ApplicationPackages`), Infrastructure never the Cli, none the
  tests, so any package added to an inner project fails here, one named `System.*` included: the framework is told by
  name (`IsFramework`: `System`, `System.*`, `netstandard`, `mscorlib`) less the files beside the four in the test's
  output folder (`AppAssemblies.PackagesBeside()`; the build copies every package there and never the shared framework,
  which the runtime loads from the dotnet folder), so `System.CommandLine` or `System.Security.Cryptography.ProtectedData`
  (Windows' DPAPI) referenced by Core or Application counts as a package, and
  `The_packages_beside_the_four_are_read` pins that the set read holds those two; (2) banned in Core and Application:
  `System.IO.File`, `Directory`, `FileInfo`, `DirectoryInfo`, `FileSystemInfo`, `FileStream`, `RandomAccess`, `DriveInfo`
  and `FileSystemWatcher`, a `StreamReader` or `StreamWriter` opened by a path, every member of `Path` but the string work
  in `AllowedPathMembers` (`Combine`, `Join`, `GetFileName`, `GetFileNameWithoutExtension`, `GetExtension`,
  `ChangeExtension`, `HasExtension`, `GetDirectoryName`, `GetPathRoot`, `IsPathRooted`, `IsPathFullyQualified`,
  `TrimEndingDirectorySeparator`, `EndsInDirectorySeparator`, `GetInvalidFileNameChars`, `GetInvalidPathChars` and the
  separator fields, so `Path.Exists`, `GetFullPath`, `GetRelativePath`, `GetTempPath` and `GetTempFileName` are banned),
  `Process` and `ProcessStartInfo`, every member of `Environment` but `NewLine`, `AppContext.BaseDirectory`, `AppDomain`,
  a named `EventWaitHandle`, `Mutex` or `Semaphore` (the unnamed ones and `SemaphoreSlim` stay allowed), `Console`,
  `Marshal`, any method with a P/Invoke import or type with `ComImport`, and the prefixes `Microsoft.Win32.`,
  `System.Net.Http.`, `System.Net.HttpListener`, `System.Net.Sockets.`, `System.IO.Pipes.`, `NAudio.`, `Windows.`,
  `Refit.`, `System.CommandLine.`, `Microsoft.Extensions.Configuration.`, `Microsoft.Extensions.Hosting.` and
  `Microsoft.Extensions.Http.`; (3) banned in Core alone: `Microsoft.Extensions.*`, `JsonSerializer` and
  `JsonSerializerContext`, `Thread` and `Timer`; (4) allowed in Core by not being banned: the `System.Text.Json.Serialization`
  attributes and `JsonElement` (`RecordRequest`, `SourcePlan` and `RunRecord` keep `[JsonExtensionData]`, `ClassSource`
  and `FollowRule` their `[JsonPolymorphic]` `kind` names, since the run files outlive versions); (5) every type sits in
  its assembly's root namespace or below (`ClassScribe.Core`, `ClassScribe.Application`, `ClassScribe.Infrastructure`,
  `ClassScribe.Cli`), which catches a file merged into the wrong project with its old namespace, passing over the
  compiler's embedded attributes, Refit's generated namespaces and the exe's `Program` (`GeneratedNamespaces`); (6) the
  four assemblies carry one informational version (`BuildInfo` reads Application's; the kit reads the exe's product
  version, the same string); (7) the Cli assembly is named `ClassScribe` and `ProgramFiles.Dll` in Core is
  `ClassScribe.dll`, which a recorder or camera process under dotnet starts. The allow-list `AllowedInner` gives a reason
  for each entry, as `// Deliberately not rethrown:` does for catches; it holds one, `Environment.CurrentManagedThreadId`,
  which the compiler's iterator state machines use. `RepositoryLayoutTests` checks the folders with
  `Directory.EnumerateFiles` (never `git ls-files`, so it starts no process and catches an untracked file a merge left
  behind), skipping every `bin` and `obj`: every `.cs` file under `src` sits in one of the four project folders, each of
  them holds its project file, no glossary sits under `src`, and no project file under `src` copies one to its output.
  `AppAssemblies.All` lists the four assemblies, innermost first, for the architecture test and for `SettingsReloadTests`'
  guard on `IOptions<T>`, which scans all four (architecture 6.4). Namespaces follow the projects (question 3:
  `ClassScribe.Core.Audio`, `ClassScribe.Application.Audio.Microphones`, `ClassScribe.Infrastructure.Desktop.Windows`,
  `ClassScribe.Cli.Commands`, `ClassScribe.Cli.Hosts.Recorder`; the tests keep `ClassScribe.Tests`), and so do the log
  categories of every `ILogger<T>` (`ClassScribe.Infrastructure.Drive.DriveClient` and the like), which no written line
  carries: `DriveSecretsTests` matches Drive's by `.Drive.`, and the one example of a `Logging:LogLevel` entry, in the
  settings command's help and the README, names `ClassScribe.Infrastructure.Drive`. The log applies an entry to every
  category that starts with it, so an entry under `ClassScribe.` that none of the four root namespaces starts with or is
  started by (`ClassScribe.Drive`, written before the move) filters nothing: `SettingsCheck.FindWarnings` warns about it
  once, naming the roots (`SettingsCheck.LogCategoryRoots`, pinned to `ArchitectureRules.RootNamespaces`) and the
  `settings reset` command; `ClassScribe` alone still covers everything.
- HTTP APIs are called through Refit interfaces registered with `AddRefitGeneratedClient` (Refit's
  source-generated client, given the `ElevenLabsJson` System.Text.Json context), not requests built
  by hand with `HttpClient`. Google's are `IGoogleOAuthApi` (`GoogleOAuthJson`, snake_case: token and
  revoke, URL-encoded forms) and `IDriveApi` (`DriveJson`, camelCase web defaults), registered in
  `Drive/DriveServices.cs` with the base address from `Drive:OAuthBaseUrl` / `Drive:ApiBaseUrl` read
  when the client is made, `HttpClient.Timeout` off (each call has 60 s of its own), no redirects,
  the keep-alive connect callback, and `RemoveAllLoggers()` so Microsoft.Extensions.Http never logs
  their requests. The API version is a path parameter of every ElevenLabs and Drive route
  (`/{apiVersion}/speech-to-text`, `/drive/{apiVersion}/...`, `/upload/drive/{apiVersion}/...`), filled
  from `ElevenLabs:ApiVersion` and `Drive:ApiVersion` by `ElevenLabsClient`, `ElevenLabsAccount` and
  `DriveClient`; `ElevenLabsApi.WrittenFor` and `DriveApi.WrittenFor`, beside the routes, are the
  versions the request and answer shapes were written for. Every call returns `HttpResponseMessage`; the bearer is passed per call as
  `[Header("Authorization")]` ("Bearer " + token, as the ElevenLabs key is), so `DriveClient` can
  renew a refused token (401) once and repeat the call. YouTube's is `IYouTubeOEmbedApi` (`YouTubeJson`,
  the record path's titles, `GET /oembed?url=<watch link>&format=json`), registered in
  `YouTube/YouTubeServices.cs` with the base address from `YouTube:OEmbedBaseUrl` read when the client
  is made, `HttpClient.Timeout` off (`OEmbedTitles` gives each read 10 s of its own and starts them at
  least a second apart), a `SocketsHttpHandler` with `UseCookies = false`, `YouTubeHostGuard`, and
  `RemoveAllLoggers()`; `OEmbedTitles.RefitSettings()` lets a failed connection come through as its own
  `HttpRequestException`. It returns `HttpResponseMessage`, so a video YouTube does not answer for is
  no title, never thrown. yt-dlp is a program, not an HTTP API: it runs through `IToolRunner`.
- A type is named by its simple name with a `using` line for its namespace, never with its area in front (`WebcamHold`,
  not `Camera.WebcamHold`; `RecordingFiles`, not `Storage.RecordingFiles`; `RunState`, not `ClassScribe.Runs.RunState`), in
  code and in XML-doc crefs alike: the trial move (architecture 11.6, `design\final\trial-move.md`) showed that a partial
  name survives the move of 10.2 and 10.3 only inside its own project, and that a file sees its area's other projects only
  through `using` lines. An alias must name a type in full (`using Condition = (string Name, ClassScribe.Runs.AttentionLevel
  Level, ...)`), and the move's namespace script rewrites those itself. A private method that would shadow a use case's
  name is named apart (`CommandLineTree.YouTubeCommandTree`, as `ClassCommandTree` and `SettingsCommandTree`). A Core file
  that uses `System.IO` writes `using System.IO;` itself (architecture 1.4), so the move commit alone builds.
- No `ArgumentNullException` guards on DI-injected parameters.
- Registration, by project (architecture 5.2): Application's `ApplicationServices.AddClassScribeApplication`
  (`src/ClassScribe.Application/ApplicationServices.cs`) calls each area's `<Area>UseCases.cs`, beside its use cases
  (`Audio/AudioUseCases`, `Camera/CameraUseCases`, `Transcription/TranscriptionUseCases`, `Drive/DriveUseCases`,
  `YouTube/YouTubeUseCases` and `Commands/CommandUseCases`, which also adds the stand-in `NoStartupReport`): the use cases
  and the services they drive, scoped or one for the process as each needs. Infrastructure's
  `InfrastructureServices.AddClassScribeInfrastructure(configuration)` (`src/ClassScribe.Infrastructure/InfrastructureServices.cs`)
  calls each area's `<Area>Services.cs`, beside its adapters, in this order: `Settings/SettingsServices.AddSettings(configuration)`
  (the options bound to their sections, the check, the saved classes), `Storage/StorageServices.AddStorage`,
  `Audio/AudioServices.AddAudio`, `Audio/Microphone/MicrophoneServices.AddMicrophone`, `Camera/CameraServices.AddCamera`,
  `Transcription/TranscriptionServices.AddTranscription`, `Drive/DriveServices.AddDrive`, `YouTube/YouTubeServices.AddYouTube`,
  `Desktop/DesktopServices.AddDesktop` and `Recorder/RecorderServices.AddRecorderLog`: the Refit clients and every adapter,
  its internal types included (`LazyLoopbackCaptureProvider`, `LazyMicrophoneProvider`, `ProcessFamily`,
  `NativeProcessTree`), so Cli needs no access to Infrastructure's internals. Cli's
  `ServiceCollectionExtensions.AddClassScribe(configuration)` (`src/ClassScribe.Cli/ServiceCollectionExtensions.cs`) adds
  `TimeProvider.System`, then Application, then Infrastructure, then `Commands/CommandServices.AddCommands` (the console,
  the menus, the command line, `CommandOutput` with `SettingsOutput` and `ClassOutput`, the window's status sink) and last
  `Commands/WindowServices.AddWindow`, the window's side of a run in the background, which replaces the stand-in report. A
  new registration goes to the registration class of its type's project and area, and the
  order of a service's registrations is kept, since a later Add of the same service wins (the split of slice 3a was
  checked against a dump of every descriptor). The ports of slice 3b1 are registered beside their adapters, by
  Infrastructure's own area: `IClassFolders` and `IClassMarks` as the same singletons as `ClassFolders` and `ClassMarks`,
  and `IRecordingFolder`, `ITranscriptCopies` and `IMicCheckLog` in `StorageServices`; `ITrackWriterFactory` and
  `IAudioFileFacts` in `AudioServices`; `ITranscriptAnswers` in `TranscriptionServices`; `IFileProbe` stays in
  `YouTubeServices`, where it was first registered. The ports of runs of slice 3b2 likewise: `IRuntimeFolder`,
  `IRecordingLock` and `IRunJournal` as the same singletons as `RuntimeFolder`, `RecordingLock` and `RunJournal`, each right
  after its adapter in `StorageServices`, and `IRunLog` as the same singleton as `RecorderLog` in
  `RecorderServices.AddRecorderLog`; each is resolved through its adapter's type, so the recorder's host's replacements
  (`AppHost.AddRecorder`) and a test's `AddSingleton(runtime.Folder)` or `AddSingleton(runtime.Journal)` are followed.
  The recorder's host replaces `IRunLog` as the very instance it was given too, beside its `RecorderLog` replacement: the
  container disposes what a forwarding factory returns, and that log is `RecorderHost.RunAsync`'s own, disposed after the
  container, so that what is logged while the container is disposed, and a failure after it, still reach recorder.log. A
  port over an adapter the recorder's host owns is always replaced that way (stop point S3's fix).
  The ports of settings and classes of slice 3b3: `ISavedClasses` as the same singleton as `SavedClasses`, right after it in
  `AddSettings` (both hosts); `ISettingsStore` as `SettingsStore`, the thin adapter over the places, the file, the reloader
  and the writer, right after the writer in `AddSettingsReload` (the window alone, as the writer: a recorder never writes
  the settings, and `RecordStarter`, which takes the port for the snapshot, is never resolved there).
  The ports of the program and the prompts of slice 3b4: `IThisProgram` as `ThisProgram.Current` in `DesktopServices` (both
  hosts, one for the process), right after `IOwnProcesses`; `CommandLine`, the class over it, a singleton of
  `CommandUseCases`; `IContinuePrompt` and `ICameraPrompt` as the same scoped `ContinueQuestion` and `CameraStart`, each
  right after its adapter in `WindowServices`; and `IRunStarter` as the same singleton as `ICommandRunner`, right after it in
  `CommandServices` (the registrations before and after each slice are compared from a dump of every descriptor).
  `AppHost` adds the settings reload (`SettingsServices.AddSettingsReload`: the places, the file, the
  reloader, the one `SettingsWriter` and the `ISettingsStore` over them), since it builds the configuration the reload works on;
  `AddSettings` registers `SavedClasses` (with `ISavedClasses`) and `IOutputFolderFacts` beside `SettingsCheck`, `CommandUseCases`
  the settings and class commands, and `CommandServices` `CommandOutput` with `SettingsOutput` and `ClassOutput`, which
  write what the two commands answer, and menus C and S
  (`ClassesMenu`, `SettingsMenu`, per run). `AppHost.Build` validates scopes and every registration
  (`ValidateScopes`, `ValidateOnBuild`), so a per-run service taken outside a run's scope, or one
  that cannot be made, fails at once. `AppHost.BuildRecorder(start, apiKey, log, folder, places,
  time, configureServices)` builds the recorder's host from the same registrations plus `AddRecorder`
  (the clock, the log it was given as `RecorderLog` and as `IRunLog`, ClassScribe's own folder, the link's console, no
  window signals, `LinkStatusSink`, `RecorderResultShower`, the server, the
  alerts and the process starter in place of the window's), from the request's settings snapshot and
  the key alone: no appsettings.json, no environment source, no console logger, scopes validated but
  not the whole container. There is no `Link/LinkServices.cs`: the view makes its own client, the
  server is the recorder host's.
- Every start of a recording is a `RecordRequest` with a `StartOrigin` (`src/ClassScribe.Core/Runs/Requests.cs`),
  through `IRunStarter.RecordAsync` (`src/ClassScribe.Application/Runs/IRunStarter.cs`, the one call `ICommandRunner` extends and `CommandRunner`
  answers) to `IRecordStarter` in a scope of its own; `ICommandRunner` no longer has `RecordAsync(string, bool)`. Run ids are made only by `RunIds.New` and checked with
  `RunIds.IsWellFormed` (`ProcessArguments.IsRunId` is the same rule) before they name a file, a
  pipe, an event or a command line. Run and request files go through `RecorderJson`, which writes
  enums by name, names each class source by `kind`, and passes over unknown fields; a field added to
  `RunRecord`, `RecorderState` or `RunOutcome`, which also travel on the link, needs a default, or
  older windows cannot read it, and anything else needs a new `LinkProtocol.Version`.
  `RecordRequest.Video` (`VideoRequest`: link, ID, title, channel, how obtained, the batch's folder,
  the browser's process id and start time, position, count, batch id, and the defaulted
  `ChannelHandle`, `Tool`, `StartSeconds`) is last, defaults to null and is written only when set
  (`[JsonIgnore(WhenWritingNull)]`), so a class's run and request files are byte for byte as before;
  a run with it is a video's (`RunView.IsVideo`) and never a class. `RecordRequest.ClassName`, after
  it, names the saved class a run was started from (null otherwise, written only when set); it only
  names the class in the run's first line. Its end causes are new
  `RunEndCause` strings (`VideoClosed`, `VideoEnded`, `ClassStarted`, `VideoSilent`, `NothingPlayed`,
  `VideoLimit`), and its class source is `ClassSource.App("chrome")`, so no new source kind or stored
  enum value reaches a run file. `StartOrigin.VideoBatch` (never stored) is a batch's start of one of
  its videos: a new recording without a question, its run shown as the batch's own, another run
  explained, never shown.
- The link (`src/ClassScribe.Infrastructure/Link`): one named pipe per run, `ClassScribe-<run id>` (`LinkProtocol.PipeNameFor`),
  `CurrentUserOnly`, the first instance, up to 4 windows, lines of UTF-8 JSON of at most 64 KB; the
  server never logs, waits or raises events inside its `RecorderLog.Written` handler, and drops a
  stalled window on the thread pool. `RecorderLog` (the adapter of `IRunLog`; `Written`, `Since` and
  `WaitForFollowers` are the link's and stay on the adapter) takes no `ILogger` (the provider writes to it,
  which would be a cycle); followers of `Written` are called in the background in `Seq` order and
  must not log, block or throw.
- No sound, ever: never write BEL anywhere; terminal sequences end with ST (`ESC \`). The session
  window (`SessionWindow`) keeps its title `ClassScribe`, which Windows' shutdown screen shows;
  `ShutdownBlockReasonCreate`, `Destroy` and `Query` run only on the window's own thread.
- Settings in code: the options classes (`RecordingOptions`, `ElevenLabsOptions`, `TranscriptOptions` and `YouTubeOptions`
  in `src/ClassScribe.Application/Settings`, `DriveOptions` in `src/ClassScribe.Application/Drive`), plain classes with
  defaults, and the catalog that reflects over them and does no I/O (`SettingsCatalog` with `SettingKey` and `SettingKind`,
  `DefaultSettings`, `DefaultChanges`, `ClassChoices`, and a settings file as the use cases see it: `SettingsText`,
  `SettingsOrigin`, `SettingsVerdict`, `SettingsEdit`, and `SettingsDocument`, the tree the writer changes) are
  Application's, behind the ports `ISettingsStore`, `IStagedSettings`, `ISavedClasses` and `IOutputFolderFacts`; binding
  them to their sections (`SettingsServices.AddSettings`), checking them (`SettingsCheck`, which reflects over every options
  type), and reading, staging and writing the files (`SettingsPlaces`, `SettingsFile`, `CheckedSettingsSource`,
  `SettingsReloader`, `StagedSettings`, `SettingsWriter`, `SettingsStore`, `SavedClasses`, `OutputFolderFacts`,
  `ApiKeyFromEnvironment` and `SettingPaths`, whose `Expanded` expands environment variables) are Infrastructure's, in
  `src/ClassScribe.Infrastructure/Settings`. Singletons read `IOptionsMonitor<T>.CurrentValue` each time they use it
  (`ClassFolders`, `TranscriptFormatter`, `SourceChoice`, the Refit client's `ConfigureHttpClient`);
  per-run services, scoped or transient (the commands, `TranscriptionWorkflow`, `GlossaryChoice`, `ElevenLabsClient`,
  `RecordingSession`, `TrackSetFactory`, `MicrophoneAdvice`, `MicrophoneMenu`, `RecordingMonitor`,
  `ElevenLabsAccount`, and for YouTube `YouTubeCommand`, `YouTubeMenu`, `YouTubeBatch`, `DownloadBatch`,
  `VideoBatch`, `DownloadedAudio` and `RecordingUpload`), take `IOptionsSnapshot<T>`, read once in the
  run's scope; YouTube's singletons (`ToolLocator`, `BrowserLocator`, the oEmbed client's
  `ConfigureHttpClient`) read `IOptionsMonitor<YouTubeOptions>.CurrentValue`. Never `IOptions<T>`
  for ClassScribe's sections: it keeps the first value for the life of the process, so an edit
  would never reach that part (`No_part_keeps_the_settings_it_read_first_for_the_whole_session`
  fails on one).
- XML `<summary>` comments on every class and method, tests included. Every project sets
  `GenerateDocumentationFile` (`src\Directory.Build.props` and the test project), so a public type or member without one
  gives CS1591: an error in the test project (`WarningsAsErrors`), a warning in the app (no XML file is published, as
  `PublishDocumentationFile` is off). Keep the build at 0 warnings. A `using` line nothing needs is a warning too
  (IDE0005, from the root `.editorconfig`, run in the build through `EnforceCodeStyleInBuild`), so each file keeps only
  the `using` lines it needs. The compiler does not check private or internal members, so check those yourself; a local
  function cannot take a summary (CS1587), so give it a `//` comment. The same `src\Directory.Build.props` sets
  `InternalsVisibleTo` once, for all four projects (architecture 1.4 and 6.2): `ClassScribe.Tests` sees every project's
  internals (the COM and P/Invoke declarations it drives through fakes, the constructors that take those fakes, the helpers
  only the adapters use), and no project sees another's; no `[assembly: InternalsVisibleTo]` line is in the code.
- Errors: log at each layer with the existing logger; rethrow with `throw;` (never `throw ex;`);
  never swallow exceptions. Google Drive's parts throw `GoogleDriveException` (a `DriveProblem` and a
  message in plain words that says what to do, never a secret) and log the details at Debug; the
  command or pass that catches it (`DriveCommand`, `DriveUploadPass`, `DriveUploadCommand`) logs the message once, so the window never shows the
  same failure twice. Nothing of Drive ever reaches a run: `DriveAutoUpload` catches whatever its pass
  throws, a cancel included, and its queue write at a run's start. A deliberate exception, a catch that does not rethrow, carries a
  comment that starts `// Deliberately not rethrown:` and gives the reason, so
  `grep -rn "Deliberately not rethrown" src` lists them. A few older ones give their reason in other
  words or in the method's summary: the Ctrl+C catch in `MenuCommand.RunChoiceAsync`,
  `AudioPreparer.DeleteUnfinished`, `RecordingSession.RestartAsync`, `WasapiAudioCapture.CaptureLoop`,
  `TranscriptionWorkflow.TryPrepare` and the retry catch in `ElevenLabsClient.TranscribeAsync`. Use
  the phrase for every new one and hold it to the same standard. `docs/HANDOFF.md` section 6.4 lists
  them too, but is out of date until it is refreshed; until then the comments are the list. The
  microphone's are all phrased; the ones worth knowing: `ClassMicrophoneWatch.Run` catches
  everything on the watch's own thread (an exception there would end the process and the class; the
  failure is logged, handed on through `Failed`, and a last Unknown look makes the gate drop
  everything), `RecordingSession.PadToClock` catches on the padding timer's thread for the same
  reason, `TrackSet.Start` leaves out a track whose format is refused while the other records,
  `TrackSet.LastLook` keeps a failed look from stopping V or the stop, `MicrophoneAdvice.TryRead`
  leaves out advice whose registry value cannot be read, and the readers (`WasapiMicrophoneSessions`,
  `MicrophoneUsageRecord`, `WasapiMicrophoneProvider`) answer a failed read with a listing that says
  so, four times a second. `TrackSet.Flush`'s catch is not one: it keeps the first failure, flushes
  the other track and rethrows it as it was. YouTube's, all phrased: `YouTubeLock.TryTake` (a lock held
  for a moment is tried again; held after every try is the answer) and `ReadHolder`; `ToolLocator`'s
  PATH read and `BrowserLocator`'s App Paths read (a registry that cannot be read is passed over);
  `YtDlpCheck.VersionOfAsync` (a tool that will not start is a problem said in words); `DownloadBatch`
  (a playlist's listing or a video's information that cannot be read leaves it out; a video whose audio
  cannot be made a recording is that video's alone; a yt-dlp Windows will not start is a failure that
  stops the downloads; a staging folder that cannot be deleted waits for the prune);
  `DownloadedAudio.CleanUp`'s two and `ReadNote` (housekeeping never holds up a batch); `VideoIndex.Read`'s
  two (a folder or source file that cannot be read means that video is fetched again); `BatchTranscripts`
  (ElevenLabs' 401, 403, 402 or 429 stops the transcripts, any other failure is that recording's);
  `BatchKeys.Dispose`; `TranscriptionRuns.Begin` (a run file that cannot be written is warned about);
  `OEmbedTitles.ReadAsync`'s two (its own limit, a failed connection) and `Parse` (an answer in another
  shape): only those, so a forgotten stub fails a test through `YouTubeHostGuard` rather than becoming a
  missing title; `VideoWindow.OpenAsync` (a window that will not open is that video's); `VideoBatch`'s
  `RunAsync` (Ctrl+C becomes exit 130), `RecordAsync` (a run in this window that failed is that video's,
  its outcome read from its run file) and `ReadSource`; `WindowsVideoYield.Listen` (an event of that
  name planted by something else: Q twice still stops the video); `RecordCommand.WriteSource` (a source
  file that cannot be written must not cost the recording); `ClassFolders.VideoOf` and
  `TranscriptionWorkflow.VideoOf` (a source file that cannot be read leaves the recording described,
  and its paid answer laid out, as a class's); `DriveRoom.ReadForUploadAsync` (Drive's room only adds a line to
  the plan, or keeps a lesson's video known not to fit off Drive); and `CommandLineTree.YouTubeProblems` (a `--list` file that cannot be read is a mistake of
  the command line, said with the others).
- Windows audio objects: the device check (`WasapiLoopbackCaptureProvider`'s two listings, once a
  second for the whole class) goes through ClassScribe's own declarations in
  `src/ClassScribe.Infrastructure/Audio/Windows/CoreAudioInterop.cs` and releases every COM object it receives in a `finally` block
  (`CoreAudio.Release`). Never make Windows' device enumerator with `new` of a `[ComImport]` class or
  through a cached wrapper; use `CoreAudio.CreateEnumerator`. Windows hands out one enumerator per
  process, and a second wrapper type for it makes NAudio's `new MMDeviceEnumerator()` throw
  `InvalidCastException`. The microphone side does the same: `WasapiMicrophoneProvider` lists the
  capture endpoints from their property stores (`EndpointFacts`: the built-in array by jack subtype,
  Bluetooth by enumerator or any property naming BTHENUM, BTHLE or Bluetooth, loopback inputs by
  name) and reads the endpoint mute, `WasapiMicrophoneSessions` reads each endpoint's sessions and
  mute, and both release every COM object in a `finally`. The Core Audio declarations live in
  `CoreAudioInterop.cs` (`IAudioEndpointVolumeRaw` and `ISimpleAudioVolumeRaw` included), except
  `IAudioSessionControl2WholeRaw` in `WasapiMicrophoneSessions.cs` and ClassScribe's own
  `IAudioClient2` in `AudioClient2Interop.cs`, which `MicrophoneActivation` uses to ask for RAW
  before `Initialize` (never NAudio's `IAudioClient2`), falling back to Default when the endpoint
  does not offer it, Windows refuses it, or a stream failed to open with it before (remembered per
  endpoint for the process). Method order in these declarations is checked against the IDLs, and
  setters are declared only to keep that order. `CoreAudio.Check` throws a `COMException` carrying
  the result for every failed result, never .NET's mapped types (E_INVALIDARG became an
  ArgumentException that stopped `devices` with the Zoom app in a meeting on 18 October); NAudio's
  calls still map, so the places that turn a failed look into a listing that says so
  (`RecordingMonitor.ClassDevice`, `BluetoothMicrophoneWatch.LookNow`, `WasapiMicrophoneSessions`,
  `DevicesCommand.Read`) catch `AudioFailures.IsFailedResult`: a `COMException` of any result, an
  `InvalidCastException`, and the mapped `ArgumentException` of E_INVALIDARG, `UnauthorizedAccessException`
  of E_ACCESSDENIED and `NotImplementedException` of E_NOTIMPL by their exact type and that result, with no
  parameter named; a null or out-of-range argument, a named parameter or a derived type is a programming
  error and fails loudly (`AudioFailuresTests`). `CoreAudio.Check` takes the checked
  expression (`CallerArgumentExpression`) and notes the call in the exception (`AudioFailures.CallKey`),
  so `AudioFailures.Describe` says `GetState: The parameter is incorrect. (0x80070057)`. In the apps
  with sound (`GetPlayingAppsListing`, a `PlayingAppsListing` with its `UnreadAudioSession`s) a
  session whose state cannot be read keeps its app listed when Windows names its process; any other
  session that cannot be read, or a device whose sessions cannot be listed, is left out, noted among
  the unread and with a Debug line, and `CaptureChooser.ChooseApp` does not say a recorded app "has
  closed" while an unread session may be its. `WasapiMicrophoneSessions` reads each session's process
  first, so a listing's `Unread` (`UnreadCaptureSession`: process, name, state as far as read; null
  when the list itself failed) and its problem (`session 2 of 3 (Zoom, pid 9090) not readable:
  GetState: ...`) say whose it was; `ClassMicrophoneWatch` ignores another app's unread session, and
  judges the followed app's own stateless one by the usage record (in use: Yes, closed: No, nothing
  said or found on another microphone: Unknown); under `auto` (`FollowRule.Any`, the shipped default)
  each stateless session by its own program's entry, matched by the whole name (`ByRecord`: one in
  use is Yes, all closed is No, else Unknown; a program the last look over every microphone found
  active on another one is left to Unknown), and the look's `UsageInUse` stays null, so the gate's
  record check never acts under `auto`. `devices` prints every unread part with
  `Could not be read now:` and ends with `Not everything could be read from Windows just now`, exit 0.
  `FailedAudioCallTests` plays E_INVALIDARG through the real readers, following chrome and zoom.
- Time every interval on the steady clock (`TimeProvider.GetTimestamp` and `GetElapsedTime`), never
  the time of day, so a change of the Windows clock neither stretches nor shortens it. In tests,
  `ManualTimeProvider.Advance` moves both clocks and `StepWallClock` only the time of day; a test that
  checks that nothing happened yet reads `UntilNextTimer` (the limit still to come) or waits for
  `TimersMade` to grow (the code's next wait), never a fixed delay.
- The QPC timeline (`src/ClassScribe.Core/Audio/Timeline`, design section 3): a packet carries the performance-counter
  stamp of its first frame in 100-ns units (`AudioDataEventArgs.Qpc100ns`, null when Windows flags
  `TimestampError`, which places it by its arrival). `MediaTime` converts without overflow,
  `RecordingClock` is one per run (origin at the first packet of any track, the offset when
  continuing, pauses and cut freezes as exclusions; `Now()` is a position), and `TrackPlacer` decides
  where a packet goes (`Append`, `Pad`, `Trim`, `Drop`, `CheckFreeze`, `LongGap`, `TooFarAhead`), with
  the device position never used. `ClockState` is the clock's whole state as an immutable snapshot, whose placement is the
  clock's own code; the camera process places a lesson's frames by it (see "The lesson's picture"). A `TrackSpec` is `Contiguous` (class-only recordings: audio appended
  as it comes, as always, with a gap note at `Transcript:ShowGapsLongerThanSeconds`) or
  `ClockAligned` (both tracks with the microphone, and the microphone in person: padded with zeros
  by a one-second timer to 1.5 s behind the clock, in chunks of at most a second, and to one end
  position at the stop). A `FreezeWatch` freeze is cut from the clock by `TrackSet.OnFrozen`, which
  must never call into a session (lock order: a capture thread holds the session's gate while it
  asks for the freeze look). Drift corrections count only up to `TrackPlacer.DriftLimit` (200 ms).
  Before any other path moves onto the clock, run the four-hour soak (below).
- The microphone gate (`MicrophoneGate`, pure) judges each microphone packet by the watch's looks on
  both sides of its stamp (`Write`, `Drop`, `Undecided`); `RecordingSession.SetGate` holds undecided
  packets, judges them again on `Changed`, every second and at the stop, and drops any held longer
  than `LongestHold` (2 s); a packet that arrives while others are held waits behind them, so none
  overtakes another (a look can land between two judgements, since the watch's thread records it
  without the session's lock). `ClassMicrophoneWatch` looks every 250 ms on its own MTA thread
  ("ClassScribe microphone watch"), over every microphone every 10 s, and hands `WatchLook`s to the
  gate through `TrackSet`, which starts the watch at the session's first pick (a track that starts
  switched off still looks), follows it to another microphone, makes a last look before V arms and
  at the stop, and tells `MicrophoneSource.FollowApp` which microphone the class app listens on.
  The chooser (`MicrophoneChooser`) pins by endpoint id, falls back after 10 s and moves to the
  class app's microphone; the gate is armed at the start only for `ClassAppMute.Visible` or
  `FollowRule.None`.
- TDD with NUnit and Moq: write the failing test first. Run `dotnet test` before handing code over.
  For the real host with stand-ins, use `AppHost.Build(places, apiKey, configureServices)`, with
  `new SettingsPlaces(<temporary folder>, userFolder: null)` or a `SettingsFileFixtures`' places as
  `places`, never `SettingsPlaces.ForThisUser`; `configureServices` runs last. `TestDoubles.cs` has
  `TestOptions<T>` (one object for `IOptions`,
  `IOptionsSnapshot` and `IOptionsMonitor`; `Set` fires `OnChange`), `TestSettings` (a real gate
  over a temporary `appsettings.json`) and `Printed` (the command lines this test process prints, `CommandLine` over the
  real `ThisProgram`, `Printed.Program`, which a subject that takes `IThisProgram` or `CommandLine` is given). Write hours of audio with `WavSegmentWriter` at 10 Hz, so
  the files stay under a megabyte. Every test that builds the app with `AppHost.Build` and runs a
  command or the menu must register a `TestRuntime`'s `RuntimeFolder`
  (`AddSingleton(runtime.Folder)`): the start-up report, the runner and the starter read and mark
  the run files, and the real `%LOCALAPPDATA%\ClassScribe` may hold a class in progress. One that
  reaches Google Drive also registers a `RoamingFolder` in a temporary folder
  (`AddSingleton(new RoamingFolder(temp))`), or replaces `IGoogleCredentialStore`, and puts
  `FakeGoogle` in as every HTTP client's primary handler (`ConfigureAll<HttpClientFactoryOptions>`),
  with `FakeBrowser` and `FakeLoopbackReceiver` in place of `IBrowser` and `ILoopbackReceiver`, so
  no browser opens and nothing reaches Google. The Drive fakes are in `TestDoubles.Drive.cs`:
  `FakeGoogle` (an in-memory Google with state: the token endpoint with the S256 check, refresh,
  revoke, `invalid_grant`, `invalid_client`; about; folders with app properties and the root query;
  faults with `Fail`, `Unreachable` and `Hold`; every secret it saw in `Secrets`, upload ids
  included; and for the uploads files with content and MD5 (`AddFile`, `Md5Override`,
  `WrongMd5For`), resumable sessions that keep the bytes and answer 308 with `Range`, and 400 for a
  piece that is not the last and not a multiple of 256 KiB, as Drive refuses it (`PieceMultiple`)
  (`Sessions`, `SessionHost` for an address elsewhere), the folder searches by name and by parent,
  and the faults `FailWhen`, `CommitOnlyNext`, `DropNextAfter` and `ExpireSessions`; `Destructive`
  lists any request that could change or remove something on Drive, which must stay empty), `FakeBrowser`
  (`BrowserPlay`: Allow, Deny, WrongStateFirst, FaviconFirst, Nothing, CannotOpen),
  `FakeLoopbackReceiver`, `LocalHttpBrowser` (a real GET to the real listener on 127.0.0.1),
  `FakeCredentialStore`, `CapturingLoggerProvider` and `GoogleWorld`, which puts them together with
  the Refit clients, a `ManualTimeProvider` (read by `SignIn()` and `Command()` through
  `ZonedClock.Eastern`, so a date they show is the same in any time zone) and a `TestRuntime`, and for the uploads a recordings
  folder in a temporary folder (`WriteRecording`, `WriteAudio` at 8 kHz so a file under a megabyte
  takes several pieces), the manifests, a retry without jitter, the resumable upload in pieces of
  256 KiB (`GoogleWorld.PieceSize`, set through `ResumableUpload.PieceSize`), the pass, the freer,
  the command and `ClassFolders` with `DriveCopies`, and for the uploads after a class `AutoUpload`,
  `QueueReport` (local times in US Eastern time) and `RecorderLog` (added to `Logs`, whose
  `TestLoggerFactory.AddProvider` hands every line on, and with a recording's log named in it, it plays
  a process that still writes that log, as a recorder after its class). `FakeDriveAutoUpload` plays
  Drive's part in a run (`Calls`: "Queue", "Ended", "AfterRun", "Upload"; `OnQueue`, `OnUpload`) for
  the commands built by hand, and `GoogleOrElse` routes an app's every HTTP client to `FakeGoogle` for
  googleapis.com and to another handler, such as the ElevenLabs stub, for the rest (`EndToEndTests`).
  `RecorderHostDriveTests.cs` is a part of `RecorderHostTests` (partial) that signs the real recorder
  host in to `FakeGoogle` through `SignedInToDrive`. The recorder's seams for tests are
  `RecorderHost { Folder, Places, Time, ConfigureServices }` (`Places` is required: a test gives its
  own temporary places) and `AppHost.BuildRecorder(start, apiKey, log, folder, places, time,
  configureServices)`. Never set the real console title, write a
  sequence to the real console, flash a visible window, or read the console in a recorder. Two
  NUnit pitfalls: `Does.Contain`, `Does.StartWith` and `Does.EndWith` compare with the current
  culture, where ICU ignores control characters, so check those ordinally
  (`text.Contains('\a')`, `EndsWith(..., StringComparison.Ordinal)`); and a `[TestCase]` whose
  arguments hold a control character gets a name VSTest cannot match, so under a `--filter` it
  silently never runs. A burst of log lines larger than `RecorderServer.QueueCapacity` (2,000) drops
  even a window that is reading, so tests write in realistic chunks. The microphone's fakes are
  `FakeMicrophoneProvider` and `LaptopMicrophones` (this laptop's five capture endpoints),
  `FakeMicrophoneSessions`, `FakeMicrophoneUsageRecord`, `FakeProcessNames`, `FakeOwnProcesses`,
  `ScriptedLooks` and the `Laptop` fixture (`TestDoubles.MicrophoneGate.cs`), `FakePacketGate` and
  `JumpingClock` (`TestDoubles.Timeline.cs`), `FakeAudioClient`, `FakeAudioClient2` and
  `FakeAudioClientActivator` (`TestDoubles.Capture.cs`), the fake enumerator, endpoints and stores of
  `TestDoubles.MicrophoneListing.cs`, `FakeRegistryValues`, and `FakeMicrophoneRecording`
  (`TestDoubles.MicrophoneRun.cs`); `FakeAudioCapture.Deliver(audio, qpc, flags)` stamps packets.
  `RecorderSoakTests` is the accelerated soak of design 5.5 through the real `TrackSetFactory`, watch
  and gate on `ManualTimeProvider`: twelve simulated minutes run with the suite, and the four-hour
  case is `Explicit` (it writes about a gigabyte of audio to a temporary folder and takes minutes).
  YouTube's fakes: `TestDoubles.YouTube.cs` (`YouTubeTestRuns`, `FakeFileProbe` with `Versions`,
  `FakeVideo`, `FakeToolRunner`, which plays yt-dlp per call, writes the files it would (the info JSON,
  the staged WAV, `done.txt`) and checks every argument list with `YtDlpArguments.ProblemWith`,
  `InstantPace`, `FakeVideoTranscriber`, `FakeDriveRoom` and `YouTubeWorld` for the download path),
  `TestDoubles.VideoWindows.cs` (`FakeVideoWindows` with `FakeCloseAnswer`, `FakeVideoBrowsers`, which
  wires `FakeProcessStarter`'s browsers to `FakeProcessLiveness` and the windows, and `FakeVideoYield`)
  and `TestDoubles.VideoBatch.cs` (`PlayedVideoRun`, `VideoWorld` for the record path).
  `FakeProcessStarter` gains `StartBrowser`, `Browsers`, `RefuseBrowsersWith`, `BrowsersEndAtOnceWith`
  and `FakeLaunch.Program`, `Arguments` and `WorkingFolder`; `FakeProcessCreationApi` gains `OnStart`,
  `ToolParts`, `RefusalFor` and `WriteTo`; `FakeRegistryValues` gains `SetLocalMachine`;
  `FakeRecording.Track` takes `heardSound`; `GoogleWorld` has the YouTube options. A test of the
  YouTube feature gives a `TestRuntime` (every YouTube folder derives from `RuntimeFolder`), the fakes
  above, `StubHttpHandler` for oEmbed at a stub address, and never the real `ToolLocator`'s or
  `BrowserLocator`'s folders (their `LocalAppData`, `ProgramFiles` and the like are given).
- Keep `README.md` in step when behaviour changes, including its troubleshooting rows when a log
  message is added or changed. The lines it quotes for the vendors' values are worded in
  `SettingsCheck` (the API versions' refusal and warning, the limits above ElevenLabs' own, the
  safety stop and a video's limit held by the file limit), `RecordingMonitor.WatchStops` (the safety
  stop's lines, built from `ElevenLabs:LongestFileHours`), `VideoEndWatch` (the video's limit),
  `TranscriptionWorkflow.RefuseIfTooLong` and `DownloadGuard` (the file limit's words); reword them
  with its Settings table and the rows of The settings and the menu, While recording, Transcribing and
  YouTube videos. The lines the README quotes for the cost and the Bluetooth warning are
  worded in `TranscriptionCost.Describe`, `UsageReport` (`RefusedAdvice` is a public const),
  `RebuildCommand.NothingCharged` and `RecordingMonitor.BluetoothText`; reword them and the README's
  rows (Transcribing, Making a transcript again, While recording) together. The Google Drive lines it
  quotes are worded in `DriveUploadPass` (`HowTheQueueGoesUp`, `AfterTheNextClass`, the stop and
  pause lines), `DriveQueueReport` (the menu header and the report at the start), `StartupReport`
  (the not-signed-in sentence and `To upload it:`), `RecordingUpload` (the progress, the last line,
  the MD5 and revision warnings, the folder not there), `LocalAudioFreer` (`Freed ...`, the refusals),
  `DriveMenu` (choice 9's keys), `DriveCommand` (`drive status`), `DriveClient` (Google's refusals),
  `DriveRetry` (`ClassScribe asks again in ...`), `DriveAutoUpload`, `ResumableUpload`,
  `TranscribeCommand.SayAudioIsOnDrive` and `GoogleTokens`/`GoogleSignIn`'s constants; reword them
  with the README's Google Drive rows. The examples there use today's class as it will be after
  Gulzar moves it: `Mantiq\2026-10-17\Mantiq_2026-10-17_14-04-42Z.*`, 4 files, 727 MB in
  `Sizes.Describe`'s units (1 MB is 1,048,576 bytes; its `.wav` alone is 722 MB). The YouTube lines
  the README quotes (its section YouTube videos and the troubleshooting table of that name) are worded
  in `YouTubeLinks` (`EmbeddedPlayer`, `EmbeddedPlayerAdvice`, the link refusals), `LinkList`,
  `CommandLineTree.YouTubeProblems`, `YouTubeCommand` (`AllowUnlistedNeedsDownload`,
  `AllowUnlistedNeedsCourseChannels`), `YouTubeMenu`, `DownloadGuard` (the verdicts, `ChannelUnknown`),
  `BatchVideo` (`FromEmbeddedPlayerWords`, `FromEmbeddedPlayerSaid`), `DownloadPlan` (the plan's lines,
  `CostOf` beside `TranscriptionCost.Describe`, the room and its refusal), `DownloadBatch` (the questions,
  the YES text, the Drive line, `NotStartedWords`, the summary), `YtDlpErrors`, `ToolSearch.Missing`,
  `YtDlpCheck`, `DownloadedAudio`, `VideoIndex` (the done-before words), `YouTubeBatch` (`KeysLine`, the
  lock and key refusals), `BatchKeys`, `ClassWait`, `BatchTranscripts` (the stop, the meanwhile lines,
  the projection), `UsageReport.Describe`, `VideoPlan` and `VideoBatch` (the record path's plan, prompts
  and summary), `VideoWindow.HandedOff`, `IToolLocator.BrowserNotFound`, `VideoTitle.NoTitle`,
  `VideoEndWatch` (the rules' lines, `PressPlay`), `VideoEnds`, `RecordCommand` (a video's start and end
  lines), `RecordStarter` (the yield, `Explain`, a lost video recorder), `StopSignal` (the yield's
  lines), `StartupReport` (a video named as the video), `LocalAudioFreer` (a video's audio kept),
  `SettingsCheck` (the YouTube refusals and the `LongestVideoHours` warning), `StayAwake.VideosReason`
  and `TranscriptFormatter.SourceLine`; reword them with those README rows.

## Working with Gulzar

- Balanced answers: not terse, not padded. No em dashes.
- When questions are needed, ask them all at once, as multiple choice where possible.
- Cite sources for research and other non-code answers.
- Arabic terms in IJMES transliteration (this governs your glossary in `%APPDATA%\ClassScribe` and the example in `examples/glossary`).

## Git flow, worktrees and dry runs

This round's plan is `design\final\build-plan.md` in the durable home,
`C:\Users\gdala\OneDrive\Documents\ClassScribe design\` (outside the repository, kept by OneDrive, out of `%TEMP%`,
which Storage Sense clears): the three designs beside it, the probes, the prototype scripts and the earlier stand-ins
(`testplayer`, `livetest`) are there too. Where the plan and a design disagree about order, branches or versions, the
plan wins.

- `main` holds released versions only: every commit it points at is tagged `vX.Y.Z`. It moves only by
  `git merge --ff-only` to a finished release or hotfix branch, in the repository's checkout
  (`C:\Users\gdala\OneDrive\Documents\GitHub\Repos\Class-Transcribe`), which stays on `main` and clean, because
  Gulzar's class launchers build its working tree (`Start-Class.ps1`, until step 2's launcher). Nobody edits files there:
  agents only fast-forward `main`, tag and push, with his OK, when it is clean and no class runs (and not within an hour
  of one); `docs/HANDOFF.md` is the one file they may write there. `main` stays GitHub's default branch.
- `develop` is the integration branch (worktree `%LOCALAPPDATA%\csw\develop`). Each step is a `feature/<name>` branch
  from `develop`, in a worktree under `%LOCALAPPDATA%\csw` (never `%TEMP%` or OneDrive, and never deep: the Windows
  target, `net10.0-windows10.0.19041.0`, pushes the test project's output paths past MAX_PATH in a deep folder, such as a
  session's scratch folder, so a build there needs a short `-p:OutDir=<folder>`), committed at the end of every
  session after `git status` has been read for deletions nobody made. Before its merge it is rebased on `develop`, the
  suite run again, and `git range-diff` read, so that a conflict resolved in the rebase is reviewed too. It is merged
  with `--no-ff` when green and reviewed, so `git revert -m 1 <merge>` takes it out again (and reverting that revert
  brings it back). A feature goes into `develop` only when it may reach a class with the next release.
- A release is `release/<version>`, cut from `develop` once its steps are merged, one open at a time. The cut is meant
  to be one commit: the `<Version>` line in `src/Directory.Build.props`, which the four projects share (before the move
  of architecture slice 4 it was in `src/ClassScribe/ClassScribe.csproj`) (and, from step 1, the version of each
  `DefaultChanges` row left empty); 0.5.0's took two (`1bebef6`, whose `DefaultChangesTests` failed on the row's version,
  then `5daac45`), so the suite is run on the cut before its dry run. Its checklist names the vendors' defaults (architecture 7.1): one the release moves
  (an address, an API version, the model, a price, one of ElevenLabs' limits) has a `DefaultChanges` row, as
  `DefaultChangesTests.A_vendors_default_that_moves_needs_a_row` demands. Then its weekday dry run through the kit, from the release's own worktree, which
  records the commit it tested. The finish: `main` merged into it first when `main` holds a hotfix it lacks, and the
  suite run again; when its head is not the commit the dry run tested (that merge, or a fix after the dry run), a
  five-minute kit run (`-Short`) on the head first; then in the checkout `git merge --ff-only release/<version>`,
  `git tag -a v<version>`, the push (his OK); the release merged into `develop` with `--no-ff`; the branch and its
  worktree removed; and, until step 2, the release built and installed the day before its first class with
  `Start-Class.ps1 -Name "Mantiq" -NoRecord`, away from any class (from step 2, the installer).
- A hotfix is `hotfix/<version>`, from `main` (or from the head of a release `main` is about to reach), with its own
  weekday dry run (short when the fix is narrow), finished as a release is, then merged into `develop` and into a release
  then open. In the window from the move (architecture slice 4, `feature/layers-move`) until the release that carries
  it reaches `main`, a hotfix from `main`, which still has the old layout, is not merged into `develop` (architecture
  10.5): each of its commits is cherry-picked without committing (`git cherry-pick -n`), each file it changed is found
  at its new path through the path map, `move-map.tsv` in `C:\Users\gdala\OneDrive\Documents\ClassScribe
  design\design\final\architecture-analysis\move` (one row per file: old path, new path, project; the file's new
  namespace and using lines come from the file itself), the edit is made there by hand where the pick could not place
  it, the suite is run (`WindowsAudio` too for an audio file), and the result is committed as `Apply hotfix <version>
  on develop`, naming the hotfix's commits. Its change to `<Version>` in `ClassScribe.csproj`, which `develop` no longer
  has, is resolved by keeping the deletion and setting the version in `src/Directory.Build.props`; a file the hotfix
  adds stays at its old path, where `RepositoryLayoutTests` names it, and is moved with `git mv` to its project and
  given its namespace. `release/0.5.0` (`5daac45`), left open and frozen while the move was made, is treated the same:
  a later fix to it is applied on `develop` by hand along the map. The move leaves `src\ClassScribe\bin` and `obj`
  (ignored, with generated `.cs` files in `obj`) and empty folders behind in every worktree that checks it out
  (architecture 10.2): slice 4 deleted them in its own worktree; in the main checkout they go, with Gulzar's OK, after
  the release that carries the move is fast-forwarded to `main`. `RepositoryLayoutTests` skips `bin` and `obj`, and the
  launcher and the kit find the project by its file, not its folder, so that is tidying, not a need.
- Versions: every release takes a new minor number, a hotfix the next patch number of the release it fixes. Version
  strings are set only on release and hotfix branches, never on a feature branch. 0.3.3 is `43ba9c6` with no version
  line, so its build shows `1.0.0+<commit>`.
- The way back on a class day, until step 2: the previous tag checked out in the checkout
  (`git switch --detach v<previous>`), which `Start-Class.ps1` then builds; `git switch main` once a hotfix is finished.
  Never during a class, nor within an hour of one. For 0.5.0 the previous tag is `v0.4.0` (`8acbdd9`): `hotfix/0.4.1` was
  never released and 0.5.0 carries its Zoom fix, so there is no `v0.4.1`. The seven steps (nothing of ClassScribe
  running, the checkout, the launcher with `-NoRecord`, Auto Sleep off or the array since 0.4.0 has no hold, the Zoom app
  quit, 0.4.0 reading only the file beside its exe, and back with `git switch main`) are in the README under "A program
  to double-click" and on the card beside his launchers, `C:\Users\gdala\ClassScribe\Way back to 0.4.0.txt`.
- Dry runs, and every manual run of a side build, go through the kit in `tools\dry-run` (its `README.md`).
  `Start-DryRun.cmd`, double-clicked by Gulzar in Explorer so that the window and its recorder start outside any job,
  publishes the worktree to `%LOCALAPPDATA%\Programs\ClassScribe-Dry` (its project found by file,
  `Get-ClassScribeProject`: `src\ClassScribe.Cli\ClassScribe.Cli.csproj`, or the older `src\ClassScribe\ClassScribe.csproj`
  of a branch from before the move, so the kit still dry-runs one, and a worktree with neither is refused; never through
  an installer, never single-file), checks that every file the build needs is there (`Get-MissingPublishFiles`: the
  exe's four and each runtime and native file `ClassScribe.deps.json` names, by file name; one that is missing is
  named, with Bitdefender's quarantine as the likely cause, as the missing exe was before),
  starts the stand-in for ElevenLabs (`ElevenLabsStub.cs`, a .NET 10 file-based app on 127.0.0.1), sets a dummy key,
  `ElevenLabs__BaseUrl`, `Drive__Enabled=false` and `CLASSSCRIBE_SETTINGS_FOLDER` in its own process only (never at
  user scope; builds before saved classes and the settings folder (build plan step 1) read only the `appsettings.json`
  beside their exe and pass over the variable, and the kit says which the build reads, by looking for the variable's
  name in every `ClassScribe*.dll`, `Test-BuildReadsSettingsFolder`), starts the copy once in that environment
  (`Test-Installed`: `--version`, whose answer must be the build the exe's file says; an older build without
  `--version` is started with `list` over an empty folder; a DLL missing or blocked fails the dry run here, before
  anything records), runs `record ... --new --output
  "%USERPROFILE%\ClassScribe dry runs"` (`-Mic` is `OBSBOT` by default, as his launcher records, so a dry run takes the
  path a class takes; `-Mic "Microphone Array"` on a laptop without one), checks the run
  file (`home` `Separate`), the transcript, the recording's log and the stand-in's log, compares the listings of
  `%LOCALAPPDATA%\ClassScribe`, `%APPDATA%\ClassScribe` and `%USERPROFILE%\ClassScribe` before and after, and adds the
  result to `%LOCALAPPDATA%\ClassScribe-DryRuns\runs.log`, which a release's finish reads. It refuses while a class
  records, by the run files, never by opening `recording.lock`, and while any run waits for the report at the start
  (`RunView.NeedsReport`, mirrored by the script's `Get-UnreportedRuns`), which the side copy's own report would act on.
  The stand-in is started with `--parent <the script's pid>` and ends with it. The kit's tests write their run files and
  log lines with the app's own writers (`RecorderJson`, `RecorderLogProvider.Text`), so a change to either that the
  script does not follow fails them. `-Real` (billed) and `-Player` (plays a clip aloud
  through the test player) are only at Gulzar's word, which the script asks him to type. Agents stop every ClassScribe
  process they started before a session ends, and leave none running over a class time. The class launcher
  `Start-Class.ps1` (outside git) keeps copies of the kit's `Get-MissingPublishFiles`, `Switch-Install` (its swap of a
  new install into place, which the kit defines beside the others and does not use, since it publishes into a side
  folder it removed first) and `Test-Installed`, so the kit is where they are tested (`DryRunScriptTests`, and the whole
  path with a publish in the Explicit `DryRunWholeTests`). The kit's `Test-Installed` starts the program through
  `Start-Process`, waited for, with its input, output and errors redirected to files, and returns the version it read:
  Windows PowerShell 5.1 runs a program built without a console, such as the tests' stand-in, detached when it is called
  with `&`, neither waiting for it nor reading it, which the launcher's copy does to the real exe, a console program,
  answering true or false alone; and the kit's catch in `Switch-Install` carries the `Deliberately not rethrown` comment
  its tests ask for.
- Everything in `tools\` (and in `installer\` once it exists) is ASCII only, with no byte-order mark: Windows PowerShell
  5.1 reads a script without one in the ANSI code page. Scripts there run under Windows PowerShell 5.1 (no `&&`, `??`
  or ternaries). `DryRunScriptTests` fails on any byte above 127, parses every `.ps1` with 5.1's own parser, and fails
  on a catch there that neither throws nor gives its reason after `# Deliberately not rethrown:`.

## Commands (PowerShell, from the repo root)

```powershell
dotnet build
dotnet test                                                            # with the hang guard (see below)
dotnet test --blame-hang-timeout 2m                                    # also names a test that hangs
dotnet test --filter TestCategory=WindowsAudio                         # real device list, meters, sessions
dotnet test --filter TestCategory=WindowsPower                         # a real power request, let go at once
dotnet test --filter TestCategory=AudioDevice                          # Explicit tests on real devices
dotnet test --filter TestCategory=WindowsProcess                       # real processes, pipes, windows, all of the test's own
dotnet test --filter TestCategory=WindowsRegistry                      # reads two HKCU values every profile has and one HKLM value; creates nothing
dotnet test --filter TestCategory=WindowsCamera                        # the real camera list, and a MediaCapture never initialised; opens no camera, no light
dotnet test --filter TestCategory=WindowsMedia                         # Media Foundation's sink writer on generated frames in a temporary folder; opens no device
dotnet test --filter "FullyQualifiedName~Four_simulated_hours"         # the Explicit four-hour soak; run it before any path moves onto the clock
dotnet test --filter TestCategory=DryRunKit                            # Explicit: the whole dry run with a stand-in ClassScribe.exe, in temporary folders
dotnet build --no-incremental --disable-build-servers                  # when other builds run at the same time; must report 0 warnings
tools\dry-run\Start-DryRun.cmd                                         # a dry run of this worktree; Gulzar double-clicks it in Explorer (tools\dry-run\README.md)
dotnet run tools\dry-run\ElevenLabsStub.cs -- 0 "<log>" --ready "<file>"   # the stand-in for ElevenLabs on 127.0.0.1 alone; the kit starts it itself
dotnet run --project src/ClassScribe.Cli                                   # the menu (a recording then runs in the window)
dotnet run --project src/ClassScribe.Cli -- record "Kalam" --device auto --output "<folder>"       # record; Q twice stops and transcribes
dotnet run --project src/ClassScribe.Cli -- record-only "Kalam" --app chrome --output "<folder>"   # record only
dotnet run --project src/ClassScribe.Cli -- record "Kalam" --app chrome --mic --mic-follows chrome --output "<folder>"   # the class and the microphone
dotnet run --project src/ClassScribe.Cli -- record "Kalam" --in-person --output "<folder>"         # the microphone alone, in person
dotnet run --project src/ClassScribe.Cli -- mic-check --seconds 600        # watch what apps do with the microphone; opens nothing
dotnet run --project src/ClassScribe.Cli -- camera-check "OBSBOT"                   # the cameras and the set-up; opens the camera for 5 s, shared, only after a typed Y (the user's word only)
dotnet run --project src/ClassScribe.Cli -- transcribe "C:\path\to\folder-or-file"   # --no-open opens nothing; --output for a file
dotnet run --project src/ClassScribe.Cli -- transcribe "<path>" --again    # one with a transcript; billed again
dotnet run --project src/ClassScribe.Cli -- transcribe "<path>" --keyterms "<file>"   # another glossary for this transcript, or --keyterms none
dotnet run --project src/ClassScribe.Cli -- rebuild "C:\path\to\folder-or-file"      # free, from the saved .json; --no-open
dotnet run --project src/ClassScribe.Cli -- finish-video "C:\path\to\lesson"         # a lesson's video made again, free, from its pieces; --no-open
dotnet run --project src/ClassScribe.Cli -- list                           # the recordings, newest first; --output
dotnet run --project src/ClassScribe.Cli -- devices                        # the devices and apps with sound
dotnet run --project src/ClassScribe.Cli -- settings list                  # every setting; set a CLASSSCRIBE_SETTINGS_FOLDER first, so his real settings stay as they are
dotnet run --project src/ClassScribe.Cli -- class list                     # the saved classes; class add|set|rename|remove change his file: ask first
dotnet run --project src/ClassScribe.Cli -- drive status                   # the Google sign-in; asks Google (ask Gulzar first)
dotnet run --project src/ClassScribe.Cli -- drive signin --client "<file>" # a real Google sign-in in the browser: Gulzar's to make
dotnet run --project src/ClassScribe.Cli -- drive signout                  # revoke, forget the token, keep the client
dotnet run --project src/ClassScribe.Cli -- drive upload "<file or folder>" # a real upload to Google: Gulzar's to make; no path: the queue
dotnet run --project src/ClassScribe.Cli -- drive free-space "<file or folder>" --yes   # deletes local .wav once Drive has them, checked
dotnet run --project src/ClassScribe.Cli -- youtube check                  # yt-dlp, Deno, FFmpeg, FFprobe: paths, versions, age; local only
dotnet run --project src/ClassScribe.Cli -- youtube "https://youtu.be/abcdefghijk" --name "Risala" --output "<folder>" --yes   # records as it plays; Enter before each video; a real Chrome window and YouTube (ask first)
dotnet run --project src/ClassScribe.Cli -- youtube --list "<file>" --name "Risala" --output "<folder>" --download --plan     # reads each video with yt-dlp, prints the plan (reaches YouTube: ask first)
dotnet run --project src/ClassScribe.Cli -- record --help                  # help per command; --help, -h, -?, /?, help <command>
dotnet publish src/ClassScribe.Cli -c Release -r win-x64 --self-contained false -o "$env:LOCALAPPDATA\Programs\ClassScribe"
```

- Classes run from the published copy (never single-file), with a Bitdefender ATD exception for that
  exe (README, Set up once): a recording then runs in a separate recorder process
  (`ClassScribe.exe --recorder <run id>`, under dotnet `dotnet ClassScribe.dll --recorder <run id>`;
  `RecorderLauncher` starts it through `IProcessStarter` with `CreateProcessW`: no window, its own
  process group, no inherited handles, breakaway where the job allows it, working folder
  `%LOCALAPPDATA%\ClassScribe`, the environment inherited as it is), which closing the window does
  not stop; `Program` hands `--recorder` to `RecorderHost` before anything touches the console. The
  recorder never reads appsettings.json: the window hands over `ISettingsStore.Snapshot()` (the reloader's) in the
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
  (`recorder.old.log` at 1 MB) until the recording's folder is known, then into `<base>.log`. Once
  Google Drive is set up, the recorder lives on after the end has gone to the windows, for the upload
  of its class and of the queue (step 8, below), while the next class may already record. A YouTube
  video recorded as it plays is a recorder's run like a class's (under `dotnet run`, in the window, as
  a class is); its Chrome window is started by the batch's window, never by the hidden recorder.
- `record` and `record-only` need the name and a source (`--device "<part>"`, `--device auto`,
  `--app <process>` or `--in-person ["<microphone>"]`), or `--class "<saved class>"` with options over
  the class's own in groups (`RecordClassAsync`, `ClassChoices`); a saved class's name without a
  source is pointed at `--class` (N5; the validator runs before the gate's read for a recording, so
  `ISavedClasses.FindInFile` looks in the file as it is now when the settings in force hold no such
  class). `--output` is optional for them and for `youtube` (N6:
  `OutputRoot`, the class's own folder, else `Recording:OutputFolder` as the settings in force say
  it). `--mic ["<microphone>"]`
  adds the microphone beside `--device` or `--app`, and `--mic-follows <app>|auto|none` names the
  app it follows (only with `--mic`, never `--in-person`, never ClassScribe itself; parsed with
  `FollowRule.TryParse`, the one parser `SettingsCheck` and the menu use too). One validator names
  everything missing, shows that command's help and exits 2, and nothing is prompted
  (`CommandLineTree` on System.CommandLine 2.0.13: `Tidy`, `Parse` with ClassScribe's own errors and
  exit 2, `KindOf`, `InvokeAsync` with Ctrl+C left to ClassScribe; `App` is thin: parse, then the
  gate and the start-up report for commands, then invoke). `--mic` and `--in-person` take an optional
  value, so a name right after them is taken as the microphone's, which the message points out.
  `--continue` / `--new` decide about a recent recording of the same name without a question; the
  menu asks C, N or Esc (`ContinueQuestion`, the window's `IContinuePrompt`, which `RecordStarter` takes, with its
  `ContinueAnswer` in `Runs/IContinuePrompt.cs`), and only a recording of the same `RecordingMode` is
  offered. `transcribe <path> --output` is passed on as a full path for a file that is not a
  recording's. `list`, `devices` and `mic-check [--seconds <1..86400>] [--log-folder "<folder>"]`
  work; `attach` is hidden. `youtube <link>... [--list "<file>"] --name "<course>" --output "<folder>"
  [--download] (--yes | --plan) [--no-transcribe] [--no-open] [--again] [--allow-unlisted]` and
  `youtube check`: `CommandLineTree.YouTubeProblems` names every mistake at once with exit 2 (no
  `--name`, a name nothing is left of, no `--output`, no link and no `--list`, a list file that is not
  there, cannot be read or holds no link, each word that is no link it takes, by its line, neither or
  both of `--yes` and `--plan`; without `--download` a playlist's or `/live/` link, more than
  `VideoBatch.MostVideos` (10) videos and `--allow-unlisted`; with it more than
  `DownloadBatch.MostVideos` (500)), and `YouTubeCommand.RunAsync` refuses `--allow-unlisted` while
  `YouTube:CourseChannels` is empty (exit 2), since the parser cannot see the settings.
  `CommandLine.YouTube` prints the record-path command the summaries give. Commands printed for later (`CommandLine`, a
  class over `IThisProgram` since slice 3b4, one for the process, which every class that prints one takes; its forms with
  an explicit `(processPath, appFolder)` stay static) name the program by its full
  path, keep the microphone options (`--mic`, `--in-person`, `--mic-follows` through
  `FollowRule.Word`) and escape PowerShell's `$`, backtick and double quotes.
- One recording at a time: `%LOCALAPPDATA%\ClassScribe\recording.lock` (`RecordingLock`, the
  `IRecordingLock` of the use cases, in `RuntimeFolder`, the `IRuntimeFolder`; `FileShare.None`, held only
  while capturing, so the next class may start during an upload). Run
  files in `runs\<run id>.json` (`RunJournal` with `RunHandle`, the `IRunJournal` and `IRunHandle` of the use
  cases; `RunRecord`: state, a heartbeat every 10 s, `EndedBy`,
  the outcome, `Seen`; written atomically, retried three times 100 ms apart; seen runs pruned after
  30 days, `RunTimings.KeepSeenRunsFor`) are the C5 marker and the next start's report (`StartupReport`: C through
  `IRunStarter.RecordAsync`, the runner's, with `StartOrigin.StartupReport`, T, O and F carried out by the
  report itself). `StopSignal` is the per-run event `Local\ClassScribe.Stop.<run id>`, the one stop
  that needs no pipe; a video's run also listens on its yield event, `Local\ClassScribe.Yield.<run id>`
  (`StopSignal.ListenForYield`, `TrySetYield`, behind `IVideoYield`/`WindowsVideoYield`), by which a
  class that starts makes it give way (see the YouTube bullets below). A recorder lost while a window shows it is continued by the starter
  (`RecordStarter`: a new recorder, then in the window after a second loss within 10 minutes;
  declined from an alert window, `StartOrigin.AutoContinueFromAlert`).

- `transcribe` on the command line (and a file dropped on the exe) refuses, with exit 1, a recording
  that already has a transcript or a kept paid answer unless `--again` is given, and prints the free
  `rebuild` command and the `--again` one, each runnable as printed (`CommandLine.Rebuild`,
  `CommandLine.Transcribe(path, again: true)`); menu choices 3 and 4 ask R (rebuild) or Y (transcribe
  again) instead. Every caller refuses a recording that is still being written
  (`ClassFolders.IsBeingWritten`), and `rebuild` refuses one without a saved answer. `--no-open`
  works with both commands; options go before or after the path, in any case (`App`).
  `transcribe --keyterms "<file>"` (passed on as a full path, `%APPDATA%` expanded, a relative one from
  the current folder; a file that is not there or a folder is exit 2) or `--keyterms none` sends that
  glossary with this transcript only (`TranscribeRequest.Keyterms`); `rebuild --keyterms` is refused
  with why (a hidden option whose only use is that message).
- The glossary (architecture 8.2; Gulzar's answers 11 A and 12 A): ClassScribe ships none and sends
  none by default (`ElevenLabs:KeytermsFile` `""`, with its `DefaultChanges` row from `keyterms.txt`);
  his glossary lives in `%APPDATA%\ClassScribe\keyterms-islamic-sciences.txt`, and the repository keeps
  `examples/glossary/keyterms-islamic-sciences.txt`, outside `src`, never built or published, which
  only the test project links (`RepositoryLayoutTests` checks that no glossary sits under `src` and no
  project there copies one). A relative glossary is taken from the folder of the settings file in force
  and from nowhere else (`SettingsPlaces.ResolveFile`, behind `IGlossaryFiles.Resolve` and
  `ISettingsStore.ResolveFile`): the user's settings folder once his own file exists, and before his
  first change, or while his file is missing, the program's folder, where the shipped defaults are. So
  a 0.4 build's leftover `keyterms.txt` beside the exe is read only until his first change (the README's
  glossary section and its `No glossary at ...` row say the same), which is why a glossary is named by
  its full path to be sure. `GlossaryChoice` (scoped) is the one resolver, for every run, every
  later transcription and the YouTube plans (`TermsFor`): `transcribe --keyterms`, then the
  recording's own `<base>.glossary.json` (`GlossaryRecord`: `decided` once, at the start, and one `sent`
  entry per transcript; read leniently and written whole through `GlossaryFiles`, behind
  `IGlossaryFiles`, with `GlossaryJson`), then the run's saved class (`RecordRequest.ClassName`), a
  saved class whose course is the recording's name (two that disagree give the setting's, with a
  warning), `ElevenLabs:KeytermsFile`, none. The window decides steps 3 to 6 in `RecordStarter`, where
  the saved classes are, and the request carries it (`RecordRequest.Glossary`, a `GlossaryPick`: the
  value, the full path, where it came from; written only when set, so null means the recorder's own
  setting, as an older window's request reads, and a pick with a field missing is passed over with a
  warning and decided where the run is); `RecordCommand` calls `GlossaryChoice.AtStart` after the
  start lines, in either home, inside a catch-all (`StartTheGlossary`: nothing about the glossary may
  end a class), which writes the record before any transcript (a continued recording keeps its own;
  when its decided file is gone, the run's glossary is written in its place) and returns the run's
  glossary; the run hands that to `TranscriptionWorkflow.TranscribeRecordingAsync(files, keyterms, run,
  ct)`, and `ForTranscript` sends it whenever the record gives none (unreadable, never written, or
  naming a file that is gone), since a recorder's settings hold no saved classes to decide again by;
  without a run (a later transcription), steps 4 to 6 decide. The workflow calls `ForTranscript`
  before each upload and `Sent` after the transcript is saved. Each says one line: `Glossary: <name>
  (<from>), N terms`, `No glossary: <from> is empty|says none`, or the reuse line `Glossary: <name>, as
  decided when this recording started (N terms then, M now)` / `; changed since: ...`; a decided file
  that is gone is decided again with a warning. The advice for a class's or the setting's glossary
  that is not there says that `class set` and `settings set` count from the next recording, and that
  this recording takes the file put there, or another through `transcribe --again --keyterms`. A
  glossary that is not there or cannot be read, and a record that cannot be read (left as it is) or
  written, never stop a recording or cost a transcript. The term
  rules are `KeytermRules` (pure: `Read`, `Validate`, `Words`), with the `KeytermLimits` that
  `GlossaryChoice` builds from the run's `ElevenLabs:MaxKeyterms`, `KeytermCharacterLimit` and
  `MaxKeytermWords` (`KeytermLimits.ElevenLabs` by default: 1000 terms, under 50 characters, 5 words,
  ElevenLabs' own, which a higher setting is warned against). A saved class may name its
  own (`Classes:<name>:KeytermsFile`, a file or `none`; never makes the class unusable; `class
  add|set --keyterms`, `--clear keyterms`, menu C's last question, `class list|show --json`'s `glossary`
  (the full path, `""` for none or for a value no path can hold, such as one longer than any path, which
  `ClassCommand.GlossaryPath` gives as none rather than throwing, so the JSON forms answer as the text forms
  do; null for the setting's), the menu's header for one that is not there). The `.glossary.json` is a file of the recording
  (`RecordingFiles.Glossary`; `TryParse` takes it, so Drive sends it), never an answer or a
  transcript, and like a log no sign of a class (`IsClass`, rebuild's folder listing).
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
  `devices` registration test is `WindowsAudio`, since it lists the real devices once. The
  microphone's Windows tests are `WindowsAudio` too and open nothing: `MicrophoneListingTests` lists
  this computer's microphones from their property stores and reads the endpoint mute, then checks
  that the test process owns no capture session; `MicrophoneSessionsTests` and
  `MicrophoneUsageRecordTests` read the real sessions and the real usage record (no entry for the
  test exe); `AudioClient2SlotTests` activates the default output device's audio client, never
  initialised, to pin ClassScribe's `IAudioClient2` slot. `WindowsRegistry` (`RegistryValuesTests`)
  reads values every profile has, and the machine's `SOFTWARE\Microsoft\Windows NT\CurrentVersion`
  `CurrentBuild` under HKEY_LOCAL_MACHINE, and creates nothing; `ProcessNamesTests` is `WindowsProcess`.
  YouTube's `WindowsProcess` tests: `ProcessToolRunnerTests` start Windows' own hidden `cmd.exe`
  through the real tool path (created suspended, put in a kill-on-close job, resumed, its output read
  through the two pipes; a cancel ends it at once; a process the job will not take never runs), the
  only real program the YouTube tests start; `YouTubeLockTests` hold a real `youtube.lock` in a
  temporary folder; `StopSignalTests` and `RecorderHostTests.Video.cs` use real yield events named
  after fresh run ids. The dry-run kit's tests: `DryRunScriptTests` (`WindowsProcess`) start a hidden Windows PowerShell 5.1 of
  the test's own (`WindowsPowerShell` in `TestDoubles.DryRun.cs`, from a script file, since
  `-EncodedCommand` copies every Write-Host line to the error stream as CLIXML) to parse the kit's
  scripts and run its functions over temporary folders, and check that `tools\` is ASCII;
  `DryRunStandInTests` run the stand-in, compiled into the tests from `tools\dry-run\ElevenLabsStub.cs`
  with `DRY_RUN_KIT_TESTS` (which leaves out its one line of program), on 127.0.0.1 with ClassScribe's
  own preparer, client and reader; and the Explicit `DryRunKit` category (`DryRunWholeTests`) builds a
  stand-in ClassScribe.exe without a window, which records nothing and answers `--version`, and runs
  the whole dry run with it in temporary folders, once as `-Exe` and once published by the kit itself
  from a stand-in worktree in the new layout (`src\ClassScribe.Cli\ClassScribe.Cli.csproj` beside an old
  `src\ClassScribe` holding only `bin` and `obj`), and builds the test player without starting it (about
  a minute: before merging a change to `tools\dry-run`, and never within an hour of a class, since a
  process named ClassScribe runs for a few seconds).
  `EndToEndTests` also covers in person from the command line, the class with the microphone
  following chrome through a cycle of listening and not (mixed by the real preparer), and
  `mic-check --seconds 1`, a download batch of two videos over `FakeToolRunner`, `StubHttpHandler` and
  `GoogleWorld`, and a batch of two videos recorded as they play through the real `RecorderHost`
  (the window's settings point `YouTube:OEmbedBaseUrl` at the stub; the recorder gets
  `FakeVideoWindows` and a `VideoPlayback` over the test's processes). Nothing is played or recorded. The suite holds
  5,148 tests after the move into four projects, the S5 review's fixes and the fix for a glossary longer than any path in `class list|show --json` (architecture slices 4 and 5), all passing. The uploads by
  themselves were tested in `DriveAutoUploadTests`
  (the queue at a run's start, the pass after each outcome or none, not set up, `Drive:Enabled` off,
  a failure and a cancel passed over, a transcript made later sending its new files only),
  `DriveQueueReportTests`,
  `RecorderHostDriveTests.cs` (the real recorder host signed in to `FakeGoogle`: after a transcript,
  record-only and a failed transcript, not after a cancel, never before the end has gone to the
  windows, the outcome and exit code kept when Drive fails), `RoamingFolderTests`, and additions to
  `DriveUploadPassTests` (a busy recording taken once more), `RecordingUploadTests` (the last line in
  the log that goes up), `RecordCommandTests`, `RecordStarterTests`, `TranscribeCommandTests`,
  `RebuildCommandTests`, `StartupReportTests` (U), `RecorderLogTests`, `SettingsCheckTests`,
  `ServiceRegistrationTests` and `EndToEndTests` (a class goes up to `FakeGoogle` after its
  transcript). The uploads' tests: `DriveRetryTests` (the waits on the manual clock),
  `ResumableUploadTests` (pieces of 256 KiB, a short Range, a dropped piece, 503/429, a renewed
  token, a forgotten session, a kept session in a new uploader, six days, another host, a revision),
  `UploadManifestTests`,
  `RecordingUploadTests` (what goes up and in what order, the PDFs and the upload copy left, the
  folder that mirrors the laptop's, a grown log as a revision, any other change left as it is, a lost
  manifest adopting Drive's files, an MD5 mismatch, a recording being written or with a live run,
  audio deleted after it went up), `DriveUploadPassTests` (the queue after the recordings asked for,
  the upload lock, keep-awake, the pause while a class records, a cancel resumed),
  `LocalAudioFreerTests`, `DriveUploadCommandTests` (what a path names), `FreedAudioTests` (list,
  menu choice 3, transcribe and rebuild of a freed class), `ClassFoldersDriveTests`, additions to
  `DriveClientTests`, `DriveFoldersTests`, `DriveCommandTests`, `DriveMenuTests`, `CommandLineTreeTests`,
  `SettingsCheckTests`, `ServiceRegistrationTests`, and `DriveSecretsTests` (an upload through the real
  app leaves no secret, the session's address included, in a log or a file). The sign-in's tests
  (`PkceTests`, `GoogleClientFileTests`, `GoogleCredentialStoreTests` with Windows' real DPAPI in a
  temporary folder, `GoogleTokensTests`, `DriveClientTests`,
  `DriveFoldersTests`, `LoopbackReceiverTests` with the real listener on 127.0.0.1 and the test as
  the browser, `GoogleSignInTests`, `DriveCommandTests`, `DriveMenuTests`, and `DriveSecretsTests`,
  which runs a sign-in, a renewal and a sign-out through the real app with every log at Trace and
  fails if any secret `FakeGoogle` saw is in a log line, a run file, the settings snapshot or the
  store in plain text) run with the suite; none reaches Google or opens a browser.
- The hang guard: `tests/ClassScribe.Tests/ClassScribe.Tests.runsettings` sets `TestSessionTimeout`
  to 10 minutes, and the test csproj names it in `RunSettingsFilePath`, so a plain `dotnet test`
  aborts a run that deadlocks and fails it instead of hanging. The suite normally takes about
  four and a half minutes (4 min 32 s for 5,148 tests after the move into four projects, the S5 review's fixes and
  the fix for a glossary longer than any path in `class list|show --json`, architecture slices 4 and 5, on 6 November
  2026, plus the build).
  `[CancelAfter]` cannot interrupt a blocked `Wait`, so it is no guard on its own. One flaky
  `WindowsAudio` failure has been seen once in four full runs:
  `DeviceCheckResourceTests.Listing_output_devices_leaves_no_handles_or_native_memory_behind`, which
  passed alone and in the next full run; and once, under load, a 5-second wait in
  `RecordingSessionTests.A_new_failure_after_the_session_recovered_is_reported_at_once` (22 October),
  which passed alone and in the next full run. Run such a test again before suspecting a change.
- The two `AudioDevice` tests in `WindowsCaptureTests` are `[Explicit]`: they open the laptop's real
  output devices (listening only), so run them only when the user is not in a class or an audio test.
- Publish as a folder, as above, never single-file: Bitdefender quarantined single-file builds
  (`-p:PublishSingleFile=true`) as Gen:Variant.Yogi.67941, a false positive, and the README no
  longer offers them.
- While recording: Q twice within 5 s stops (the first Q logs "Press Q again within 5 seconds to
  stop"); Ctrl+C twice within 5 s stops the same way, and after the stop one Ctrl+C cancels the
  preparation and upload (with a 2 s allowance after a stop by Ctrl+C); P pauses or resumes; M
  writes a mark to the recording's `.marks.txt`; V switches the microphone off and on
  (`IRecording.SetMicrophone`, null for a recording without one: `TrackSet` disarms the gate and
  releases the capture, or makes a last look, arms the gate and reopens; with the class app followed
  the title shows `MIC OFF`, `MIC MUTED` or `MIC WAITING` from `MicrophoneState`, see `TitleText`);
  Esc leaves the view of a background recording without stopping it (from the menu, the menu comes
  back and offers A). A window the recorder opened by itself ignores every key until Enter.
- What is recorded: a `SourcePlan` with a `RecordingMode` (`Class`, `ClassAndMicrophone`,
  `Microphone`), the class's `ClassSource` and a `MicrophoneChoice(NamePart, Follows)`;
  `TrackSetFactory` (the registered `IRecordingFactory`) makes a `ClassRecording` for the class
  alone, as always, and a `TrackSet` of two `RecordingSession`s on one `RecordingClock` with the
  class, or of the microphone alone in person. The class (`CaptureChooser`): by default the output
  device with sound, found with Windows' level meters once a second; `Recording:Device` pins a
  device; `Recording:App` records one app's process tree through Windows process loopback
  (`ProcessLoopback`). The microphone (`MicrophoneChooser` over `MicrophoneSource`): Windows' default
  unless it is Bluetooth or a loopback input, then the built-in array, then another; a name part
  picks one, Bluetooth allowed, a loopback input never; pinned by endpoint id, a stand-in after 10 s
  away, the class app's microphone first when the watch finds the app listening there; looked at
  every 10 s and on `MicrophonesChanged`. The followed app: the plan's `Follows` (menu 7,
  `--mic-follows`), else the `--app` in app mode, else `Recording:MicrophoneFollows`; `None` in
  person. A pick from menu choice 6 or 7, automatic included, holds until ClassScribe is closed
  (`SourceChoice`); without one, the settings decide as they are read again. ClassScribe's own
  processes, this one and any other ClassScribe, are left out of the apps with sound and never count
  as an app listening on a microphone (`IOwnProcesses`), so neither `Recording:App` nor
  `Recording:MicrophoneFollows` can pick ClassScribe.
- The camera in a run (camera-first step 11, the part that needs no probe): `SourcePlan.Camera` (`CameraChoice(NamePart,
  Resolution)`, a last field with a default, written only when set, so a run without a camera writes its files byte for
  byte as before; never a fourth `RecordingMode`), only in person. `record`/`record-only --camera ["<camera>"]
  [--resolution <size>]`: `--camera` only with `--in-person` (`--camera records only in person in this build: give it with
  --in-person`), `--resolution` only with `--camera` (`--resolution needs --camera`) and checked by `CameraSize.TryParse`,
  each exit 2; a name right after `--camera` is pointed out as the camera's. Menu choice 7 (now "Microphone and camera"),
  in person, lists the cameras (`MicrophoneMenu`'s optional `ICameraProvider`, read through `CameraListing.ListWithinAsync`
  in `ICameraProvider.cs`: on the pool, waited for at most `CameraListing.Limit`, 5 s, on the menu's `TimeProvider`; a list
  that does not come says `The cameras could not be listed: Windows did not list the cameras within 5 seconds.`, release
  0.5.0 review 3.1) and asks the size (`Picture size: Enter for
  720p (your setting), 1 480p, 2 720p, 3 1080p, T to type a size`), held by `SourceChoice` (`PickMicrophone(..., camera,
  size)`, `PickedCameraResolution`, `CameraResolution`; W and N drop the camera; the header adds `with the camera "..."
  (720p)`). `RecordStarter` hands a plan with a camera to its `ICameraPrompt` (`Runs/ICameraPrompt.cs`; `CameraStart` in the
  window, scoped; before the continue question, amendment 17), which resolves the size into the request (`--resolution`, else menu 7's pick, else `Recording:CameraResolution`,
  else 720p, so the recorder never reads it and the run file keeps it), lists the cameras and reads the set-up each within
  `CameraListing.Limit` on its `TimeProvider` (a list that could not be read is shown in the camera's line, `Camera "OBSBOT"
  (the cameras could not be listed: Windows did not list the cameras within 5 seconds; 720p)`, never as "not connected now";
  a set-up read that does not come is the warning `CameraStart.SetupNotRead`, and the start goes on, since the camera
  process says what is wrong once it opens the camera), then refuses with the set-up's first problem or
  `ICameraRecorder.NotReady`: in the menu `Camera "...": <why>. Enter records the sound only; Esc goes back.`, elsewhere
  `<why>. Nothing was recorded; leave out --camera to record the sound alone.` with exit 1; otherwise the menu asks
  `Enter records with the camera; S records the sound only.`, and the command line goes on. `ICameraRecorder`
  (`Camera/CameraRecorder.cs`, with `ICameraTrack`, `CameraTrackStart`, `CameraTrackState`, `CameraConcern` and
  `CameraOffsets`) is registered as `CameraRecorder` (slice 7, "The lesson's picture" below; `NotReady` only before Windows
  10 version 2004); `CameraRecorderNotBuilt` stays as a stand-in for the refusal's tests, a `RecordStarter` without a
  camera prompt refuses with its words (`ICameraPrompt.NothingStarted`), and `TrackSetFactory.Create` throws `CameraNotBuiltException` for a plan with a
  camera outside person, or without a ready camera recorder. `CommandLine.Record` keeps `--camera` and `--resolution`. Amendment 2's extension data: `RunRecord`,
  `RecordRequest` and `SourcePlan` carry `[JsonExtensionData] Dictionary<string, JsonElement>? Extra` (null when nothing
  unknown was read, so records still compare equal), with a setter, not `init`: the generated reader fills init-only
  properties through the constructor, where extension data cannot go. A build older than this one still drops what it
  does not know when it writes a run file back. Tests: `CameraMenuTests` and `CameraStartTests` (both on a
  `ManualTimeProvider`, with `FakeCameraProvider.Hangs` and `FakeCameraSystem.EncoderQueryHangs` for the limits), and
  additions to `CommandLineTreeTests`, `CommandLineTests`, `RecordStarterTests`, `RequestsTests` and `TrackSetTests`.
- The run with the microphone (`RecordingMonitor`, `MicrophoneChecks`): the advice lines at the
  checks' first look (`MicrophoneAdvice.AtStart`: privacy, the `ClassAppMute` case line, the
  following line, the microphone line, then the warnings about Vantage, ducking, Bluetooth and the
  echo, which are one Notice for 5 minutes), Vantage's mode read again every 30 s while the array is
  recorded, the echo whenever the class moves (in app mode the app's device every 30 s), the gate's
  state logged once it has held for a second, a Notice after 5 minutes of `NotOpened`, an Alert after
  10 s of `Unreadable`, the gate's `Evidence` as a Notice, a missing microphone every 30 s, only
  zeros for 2 minutes (`RecordingSession.OnlyZerosAfter`), the status clause
  (`MicrophoneChecks.StatusClause`) and, from `RecordCommand`, the microphone's summary from
  `MicrophoneGate.Summary()`. The run reads the microphone through `IMicrophoneRecording`, which
  `TrackSet` implements and `ClassRecording` does not. `TrackSet.Stop` logs the clock's own summary
  line (paused, cut, parts, drift corrections).
- Arrival watch: `RecordingSession.GetStatus` also reports `SinceLastPacket` (since the open capture
  last handed over any audio, silence and pauses included), `DeliversContinuously` (true for process
  loopback and microphones, false for device loopback) and `RecordedLevel` (Windows' meter for the
  recorded device or app at the last look; null while paused, after a failed look, when it is not
  listed, and for a microphone). The monitor's per-track `ArrivalWatch` warns when a continuous
  capture has handed over nothing for 5 s, or a device's meter has shown sound at every tick for
  10 s while nothing arrived, at most every 30 s, and the loop calls `IRecording.Reopen(track,
  reason)`, which replaces that track's open capture at the next look, in a new part, and logs the
  reason at Debug; the microphone's stall is a Notice, the class's an Alert.
- The class app on a Bluetooth microphone (`BluetoothMicrophoneWatch`, scoped, `Audio/Microphone`):
  in the modes that record the class, at the first look and every 10 s after
  (`ClassMicrophoneWatch.AllMicrophonesInterval`), it joins `IMicrophoneProvider.GetMicrophones()`
  (which microphones are Bluetooth, and the array's name for the advice; this first look makes the
  real `WasapiMicrophoneProvider` behind `LazyMicrophoneProvider` in every recording of the class,
  not only those with the microphone) with `IMicrophoneSessions.ReadAll()` (which apps hold an
  Active session on them), leaving out ClassScribe's own processes and Windows' sounds; the apps
  that count are the recorded app in app mode and the followed app when one is named, else any app
  (`RecordingMonitor.WatchedApps`). The monitor warns once per new (app, microphone) pair, naming
  the app, the microphone and the fix, worded for a headset or earbuds alike (`that puts the headset
  or earbuds into call mode`), says when the app lets go, and keeps a Notice (`chrome on a Bluetooth
  microphone`) while it lasts; lists that cannot be read keep the last uses and are noted at Debug
  once per stretch. A Bluetooth headset or earbuds used as a microphone go into call mode, and
  Bluetooth then carries the class past Windows' loopback, which is how a class recorded silence for
  8 minutes.
- Keep-awake: `IPowerRequests.Hold(StayAwake.RecordingReason, ...)` from the start of capture until
  the transcript is saved (`record-only`: until the files are closed),
  `StayAwake.TranscribingReason` while `transcribe` prepares and uploads, and
  `StayAwake.VideosReason` ("ClassScribe is working through YouTube videos") held by a YouTube batch's
  window from the plan to the summary (`YouTubeBatch.KeepAwake`; each video's run holds its own
  recording request as a class's does). `StayAwake.Needs` asks for
  the system and execution requests always, and the display as `Recording:KeepScreenOn` says
  (`OnBattery` by default), read again every 30 s with the power source. A refused request is logged
  once with advice, never thrown. `FreezeWatch` (the loop's ticks plus a 1-second timer of its own)
  logs sleeps and hold-ups of 3 s or more, told apart by Windows' interrupt time against its unbiased
  interrupt time; the arrival watch and the silence warning count from the end of the last one.
  Tests may make a real power request only if they let it go at once (the `WindowsPower` category),
  and must never change the computer's power settings; everywhere else use `FakePowerRequests` and
  `FakePowerSource`.
- Exit codes: 0 success, and Esc leaving a recording that carries on, and `mic-check` stopped by Q,
  Ctrl+C, `--seconds` or a cancellation; 1 failure, settings that fail the check included, a refused
  command (a transcribed recording without `--again`, a recording still being written, `rebuild`
  without a saved answer), another recording running, a lost recorder that was not continued,
  nothing recorded (a class track never written while the microphone's was counts, with the
  `transcribe` command for the microphone's files), `mic-check` unable to create its log, a Google
  sign-in that did not go through, `drive status` not signed in or with Google out of reach,
  `drive signout` when Google could not be asked, `drive upload` with anything left waiting (not set
  up, signed out, another upload holding the lock, a recording being recorded, Google out of reach,
  Drive full, a file refused) or a path that names no recording, and `drive free-space` when any
  recording kept its audio or Drive could not be asked; 2 a mistake on the command line, named with that
  command's help (a missing name or source, a saved class's name without `--class`, an unknown or unusable saved class, two sources, `--mic` with `--in-person`,
  `--mic-follows` without `--mic`, a bad value, an unknown word, a path that is not there, `drive`
  alone, `--client` without a file, `drive free-space` without `--yes` or a path; for `settings` and `class`, an unknown setting or class, the key, a value the check refuses, and `class show` of an unusable class or while the file is missing, while 1 is a file that is not JSON, has comments of its own, is missing (but for `class show`), cannot be written or keeps changing); 130 cancelled (Esc
  during the sign-in's wait included, and Ctrl+C during `drive upload`, which leaves it to go on
  where it stopped, or `drive free-space`; nothing reads Esc during those two, although
  `CommandLineTree`'s help for `drive upload` says "Esc or Ctrl+C stops it"). An upload after a run
  or a transcript never changes the exit code. `youtube`: 0 when every video ended as asked (recorded
  with a confirmed end or downloaded, and transcribed when asked) or was done before, for `--plan` (a
  download's even when its room does not fit), and after Esc or Enter alone went back in the menu; 1
  when anything was refused, left out, waits for a word, failed, may be cut short, was left recording
  in the background, or not transcribed, when another batch holds `youtube.lock`, when Chrome or a tool
  is missing, when transcripts are asked without a key, or when a download's room does not fit; 2 for
  the command line's mistakes, a batch found to hold more than 500 videos once its playlists are read,
  and `--allow-unlisted` while `YouTube:CourseChannels` is empty; 130 for Ctrl+C twice on the record
  path (no transcripts), Q twice or Ctrl+C on the download path, and a cancelled transcript.
  `youtube check`: 0 when all four tools are found and yt-dlp is at most 90 days old, else 1. A window closed during a background recording gives
  none; the run file in `%LOCALAPPDATA%\ClassScribe\runs` keeps the outcome for the
  next start's report (`StartupReport`). A microphone track that could not be started is left out
  of the run and the class goes on alone, with a warning at the end and exit 0.
- Recordings go to `Recording:OutputFolder` (`%USERPROFILE%\ClassScribe`; a relative path is taken
  from the user profile, see `ClassFolders.Root`), or the `--output` folder, in the course > date
  layout (Gulzar's decision of 17 October 2026): a folder per recording name, the course, and in it a
  date folder per class, the LOCAL date of the start on the run's `TimeProvider` and its
  `LocalTimeZone` (`FolderNames.DateFolderName`, `yyyy-MM-dd`), `yyyy-MM-dd (2)`, `(3)` for later
  classes of the name that day: `ClassFolders.CreateRecording` takes the first date folder that is
  free (not there, or holding no class: `IsClass` counts a file `RecordingFiles.TryParse` accepts that
  is audio on either track, a transcript or an answer, and a `.log` or `.marks.txt` only when
  `IAudioOnDrive` finds that recording's audio on Drive alone, so a false start's lone log, his PDFs
  or an empty folder leave it free, as Gulzar decided on 18 October; a file of that name is passed over, and so is one that cannot be looked into, with
  the warning `The folder {Folder} cannot be looked into ({Reason}), so this recording goes to the
  next date folder`, which the README's While recording table quotes), and bumps the second while
  any file of the base name is anywhere in the course (`CourseHasBaseName`), so base names are
  unique per course. Its deliberate catches: `IsFree` (a
  date folder that cannot be looked into counts as taken, with a warning), `HoldsBaseName` (a date
  folder that leads nowhere holds no file of the name, at Debug) and `ListRecordingsUnder` (a course
  folder that cannot be read is left out of the list with one warning). A file transcribed on its own gets the
  same layout (`CreateForFile`, the date of the transcription). A continued recording stays where it
  is: `RecordCommand` opens it with `ClassFolders.OpenToContinue(OutputRoot, Name, BaseName)`, which
  looks for the base name flat in the course folder and in its date folders (the request carries no
  folder). The old flat layout (`<root>\<name>\<name>_<stamp>Z.*`) is read everywhere and nothing is
  ever moved: every reader goes through `ClassFolders.RecordingFoldersIn(folder)` (the folder itself,
  then its date folders by name; `FolderNames.IsDateFolder` is `yyyy-MM-dd` of a real day, optionally
  ` (N)`; other subfolders such as `Slides` are passed over), so `FindNewest`, `RecordingsIn`,
  `FindContinuable`, the list (`ListRecent`, `ListAll`, one warning per unreadable course folder,
  date folders read with `IgnoreInaccessible`), `RebuildCommand.FindInFolder` and `WhereRecorded` (an
  answer kept in the root is laid out beside its recording in either layout) take a course folder, a
  date folder or an old name folder. `ListCommand.NotesOf` shows `in 2026-10-17 (2)` for a later
  class of its day (`FolderNames.LaterClassOfItsDay`). `ClassFolders.DriveLevelsOf(folder)` is the
  Drive path under `Drive:FolderName` that `RecordingUpload` uses: a date folder gives its course and
  itself, any other folder its own name, whatever the recordings folder (so `--output` folders mirror
  the same way). Files in a recording's folder that `RecordingFiles.TryParse` does not take for the
  recording's (his PDFs, slides and notes, even one named `Mantiq_2026-10-17_Notes.md`, which has no
  time in its stamp) are ignored by every listing, never uploaded, never deleted. Files are named
  `<name>_<yyyy-MM-dd_HH-mm-ss>Z` (UTC start) with `.wav`, `_partN.wav`, `.mic.wav` and
  `_partN.mic.wav` (the microphone's track beside the class, `Track.Microphone`,
  `IRecordingFolder.FindSegments(files, Track)`; in person the microphone is the main track under the plain
  names), `.marks.txt` (marks, and notes written as `hh:mm:ss.fff ~ words`, such as the resume note
  `ClassMarks.AddResume` writes, the sleep notes and the gap notes of class-only recordings;
  `IClassMarks.PartsOf` counts the parts), `.log` (everything the recording's runs logged, in both
  homes, `RecorderLog.WriteFileTo`), `.md` (the Markdown transcript), `.json` (ElevenLabs' answer),
  `.upload.wav` (the 16 kHz copy, the two tracks mixed when there are two, removed after every try,
  failed or cancelled included, unless `Recording:KeepUploadFile`) and `.unread.json` (an answer
  ElevenLabs charged for that did not become a transcript; a `.json` left without its `.md` counts as
  one too, see `IRecordingFolder.FindKeptAnswers`; menu choice 3 shows `answer kept` and asks before a
  charged retry), `.glossary.json` (the glossary decided as the recording started and what each
  transcript sent, `RecordingFiles.Glossary`; see the glossary bullet), and for a lesson with the camera its pieces, `<base>.cam.mp4` and `<base>.cam@hh-mm-ss.fff.mp4`
  (`RecordingFiles.CameraPiece(start)`, `IRecordingFolder.FindCameraPieces` in their order on the clock, `IsCameraPiece`; a sign of a
  class for the date folders; held while written for `IsBeingWritten`, the newest piece open for writing with reading
  allowed; never sent to Drive, `RecordingUpload.FilesToUpload` and `IUploadManifests.OnDisk` leave them out; a lesson with
  pieces continued only by a run with the camera, `ClassFolders.FindContinuable(..., camera)`; one without pieces is the run
  files' to decide, `ContinueQuestion.FindContinuable`: a run that asked for the camera is continued with it, a piece or
  none, and with no run file left, no piece means no camera). `Track` has no
  `Camera` value: `FindSegments` stays `.wav`'s. `RecordingFiles` owns the naming (Core, which never reads the disk; its
  `TryParse` takes the folder from the path as given, which every caller passes in full), `IRecordingFolder`
  (`RecordingFolder`) what is on disk of a recording, and `ITranscriptCopies` (`TranscriptCopies`) the copy numbers: a
  transcript is never overwritten (`(2).md`, `(3).md`), not even by two processes at once, because
  each copy's `.json` and `.md` are claimed with `FileMode.CreateNew`. `IRecordingFolder.HasTranscript` counts any
  copy; `ClassFolders` (the `IClassFolders` of the use cases) measures a recording by its longest track, reports `HasMicrophone` (`list`
  and menu choice 3 say `class + microphone`), and continues a recording only with the tracks it
  was made with. The microphone check's logs go to `<root>\mic-check\mic-check_<local stamp>.log`
  (`IMicCheckLog`, `MicCheckLog`: no `Z`, so `RecordingFiles.TryParse` never takes one for a recording, and the
  folder lists no recordings). A YouTube video's recording adds `<base>.source.json`
  (`RecordingFiles.Source`, `VideoSource`, written atomically by `VideoSourceFile`, unknown fields
  passed over): `TryParse` takes it, so Drive sends it with the recording and the readers see it; it is
  never an answer or a transcript copy; `IsClass` counts it as a trace only, so a video whose fetching
  failed never holds a date folder; `LocalAudioFreer` never deletes it; `ClassFolders.IsVideo` (a
  recording with one) keeps `FindContinuable`, the continue question and the report's C away from
  videos; `RecordedClass.Video` gives `list` and menu choice 3 their `"<title>" (YouTube, downloaded)`
  note. A batch's videos share one date folder: its first recording takes the day's free one through
  `CreateRecording`, every later one goes in with `ClassFolders.CreateRecordingIn(folder, name)` (base
  names still unique per course), and `IsFree` counts a folder with a `<segment>.wav.partial` (a
  conversion in progress) as taken, so a class that starts meanwhile takes its own.
- Transcription: `ElevenLabsAnswer` is the only reader of ElevenLabs' answers, for the client and, as
  `ITranscriptAnswers`, for rebuild. `UploadTimeLimit` gives each try 10 minutes plus the upload at
  `ElevenLabs:SlowestUploadMbps` plus a quarter of the audio, rounded to the minute, never below
  `ElevenLabs:TimeoutMinutes` (2 h of audio: 71 minutes); `HttpClient.Timeout` is off, as
  `ElevenLabsClient` holds the limit. A recording longer than `ElevenLabs:LongestFileHours` less half an
  hour (`TranscriptionLimits.LongestRecording`, 9.5 h by default) and the minute past it
  (`TranscriptionLimits.Overrun`, `Sends`: a recording the safety stop ended there runs a moment past
  its stop, and a download's audio up to 2 s past its video) is refused before anything is
  prepared, its message built from the setting, measured by the class track alone (the
  microphone's track is as long; the advice says to copy the `.mic.wav` files along). With `.mic.wav`
  files the workflow hands `AudioPreparer.Prepare(UploadTracks)` both tracks, which mixes them into
  one 16 kHz mono track from position 0 without gain, clamped at full scale, and leaves out a
  microphone track that never rose above `MicrophoneSoundThreshold` (100 of 32,767) with the line
  `Your microphone stayed silent; only the class is sent.`; a recording with only `.mic.wav` files is
  sent on its own. `TranscriptionWorkflow.RebuildAsync` lays the newest saved answer (never an
  `.unread.json` unless it is named) out again with the current marks and `Transcript` settings, as
  the next free copy, with no key and no request. A recording with a source file is a YouTube video's:
  `TranscribeRecordingAsync` and `RebuildAsync` alike hand `TranscriptFormatter.Format` its `VideoSource`
  (`TranscriptionWorkflow.VideoOf`), which heads the transcript with the video's title (escaped by
  `MarkdownText.Escape`), `Downloaded` or `Recorded` and the date, and `SourceLine` (the link, the
  channel, and for a download the upload date, length, playlist and yt-dlp version; for a recording
  its length and that ads and pauses shift the positions); in a download's transcript every
  `[hh:mm:ss]` is `[[hh:mm:ss]](<watch link>&t=Ns)` while `Transcript:IncludeTimestamps` is on.
  `RecordedLine` takes `Recorded` or `Downloaded`, and `IsRecording` counts a source file.
- What a transcript cost: right after `Transcript received`, `ElevenLabsClient` logs the estimate
  (`TranscriptionCost`: the answer's `audio_duration_secs`, or else the upload WAV's length from
  `IAudioFileFacts.AudioLength`, at `ElevenLabs:PricePerHourUsd` plus `ElevenLabs:KeytermsPricePerHourUsd`
  when keyterms were sent; dollars are never turned into credits), guarded so that it can never fail
  the paid transcript. Then `UsageReport` (scoped, one per run): `TranscriptionWorkflow` calls `Begin`
  as the upload starts (the read runs alongside, never awaited there), and `ReportAsync` is the run's
  last step but the upload to Google Drive, after `Transcript saved to` and the results are open, so nothing anyone waits for is
  delayed: in this window `RecordCommand` (when `RunBookkeeping.Home` is `InWindow`) and
  `TranscribeCommand` call it; in a recorder `RecordCommand` leaves it to `RecorderHost.RecordAsync`,
  which calls it after `server.EndAsync` has sent the end to the windows (which open the results),
  and only for a `Transcribed` outcome, while the run's scope is still open, so the line lands in the
  recording's `.log` and `recorder.log` after the window has left (`RecorderHostTests` pins the
  order on the manual clock; the EndToEnd runs check the `.log`). The reads go through `IElevenLabsAccount`
  (`ElevenLabsAccount`, transient: `GET /v1/user/subscription` on the Refit interface, one request, a 10-second
  limit of its own, the key never logged; 401 and 403 are a refusal said once at Information with the
  User permission named, everything else is unavailable, and nothing is ever thrown but the caller's
  own cancel). `ElevenLabs:ReadAccountUsage` false asks nothing; the estimate still comes. `rebuild`
  logs `RebuildCommand.NothingCharged`. A YouTube batch reads the account once for its plan
  (`UsageReport.Describe`, one line, never thrown), and after its first transcript projects the rest
  from `UsageReport.Charged` (`UsageCharge`) in the account's own credits (`BatchTranscripts.Project`);
  dollars are still never turned into credits. In tests, `StubHttpHandler` answers the subscription path only
  from `EnqueueFor(StubHttpHandler.SubscriptionPath, ...)` (a subscription request never takes an
  upload's answer, so the read beside an upload cannot steal it), and `FakeElevenLabsAccount` stands in
  for the reader where the client is mocked.
- Settings (build-plan step 1, layout-config slice 1, as camera-first adapts them): the user's own
  file is `%APPDATA%\ClassScribe\appsettings.json` (`SettingsPlaces`, decided at every read by
  `Current()`: the user's file when it is there; else, once `%LOCALAPPDATA%\ClassScribe\settings-home.txt`
  marks that it was made, the Missing state, which reads the defaults beside the program, says so
  (`DescribeMissing`) and refuses every change; else the shipped `src/ClassScribe.Cli/appsettings.json`
  beside the exe, the template, under `dotnet run` the copy in `bin\Debug\net10.0-windows10.0.19041.0`,
  where the menu warns when the project's file is newer). `CLASSSCRIBE_SETTINGS_FOLDER` names another
  user folder, for dry runs, and the menu says so. `SettingsWriter` is the one writer of that file
  (registered in `AddSettingsReload`; the use cases reach it, the places, the file and the reloader's staging and snapshot
  through `ISettingsStore`, whose adapter `SettingsStore` forwards to them, and the file's bytes staged come back as
  `IStagedSettings`, which `StagedSettings` implements with a value by name and a section's children): every change (menu S and C, S after choices 6 and 7, `settings
  set|reset|open|fill`, `class add|set|rename|remove`) goes through `Change`, under
  `%LOCALAPPDATA%\ClassScribe\settings.lock` (`FileShare.None`, 2 s), from a fresh read, checked by the
  reloader's own staging before it is written (temp file and move, the file before kept as
  `appsettings.previous.json`), then reloaded. The first change writes a full copy of every setting
  (`SettingsCatalog`, by reflection over the options classes in declaration order, plus
  `Logging:LogLevel` as a map), under a three-line `//` header, and the marker; it never writes the
  key (`SettingsCatalog.ApiKeyRefused` and `IsTheKey`; one a hand edit put in the file is taken out of it and of the copy before,
  `ApiKeyTakenOut`, and reset --all keeps no copy of a file that is not JSON and mentions one,
  `ApiKeyNotKept`) and refuses a file with comments of its own (`CommentsOfItsOwn`: any comment after
  the opening brace, and before it any that is not a line of a header in `KnownHeaders`, in order,
  written as a line comment; a later build that rewords the header adds the old one there). Keys a
  newer build wrote are kept; every later change adds the settings the file lacks at their defaults
  and says so with the build's version (`AddedLines`), and `settings fill` does that alone (refused
  with exit 1 while the user's file is missing). A changed default
  needs a row in `DefaultChanges.Rows` (old, new, version), which `settings list` and the menu's header
  show for a file holding the old one (`Recording:MicrophoneFollows`, `chrome` to `auto`, is the
  first; `ElevenLabs:KeytermsFile`, `keyterms.txt` to `""`, the second, shown as `newer default: ""`). A vendor's default
  that moves needs one too: `DefaultChangesTests.A_vendors_default_that_moves_needs_a_row` holds the addresses, the API
  versions, the model, the prices and ElevenLabs' limits to the defaults they first shipped with, or to a row. Saved classes are `Classes:<name>:<field>` in the same file (`SavedClasses`, a singleton
  reading `IConfiguration` at each use, behind `ISavedClasses` for the use cases and the menus; `ClassFields.All` in order; unknown fields kept; each class
  checked on its own by Core's `SavedClass.Check`, so an unusable one never holds up the rest or the file); `ClassChoices` applies
  the command line's or the menu's answers in groups. Menu C's E takes its Enter answers from the
  class, or for an unusable one from its raw fields (`ClassesMenu.Kept.FromFields`), so Enter through
  every question keeps them; a folder typed in menus S and C is resolved by `IClassFolders.RootFor`,
  a relative one from the user profile, as `Recording:OutputFolder` is. `settings` and `class` run without the gate
  (`CommandKind.Settings`), so a refused file can be fixed with them.
  `RunGate` reads the file again before each menu choice and each command (`SettingsReloader`): a
  staged copy is checked by `SettingsCheck`, and only values that pass reach the live settings,
  through `CheckedSettingsSource` and a reload; nothing watches the file. A refused file keeps the
  last accepted values: the menu shows the problems and refuses choices other than recordings (the
  menu comes back), a command exits 1. A recording (`GateUse.Recording`: menu 1, 2 and C, the report's
  C, `record`, `record-only`; `CommandKind.Recording`) is never refused: it drops each refused key
  and keeps the rest (`SettingsVerdict.Dropped`, logged with the value used in its place), or, for a
  file that is not JSON or cannot be read, keeps the settings read last, or the defaults
  (`SettingsVerdict.Kept`). It does not stop the start: the first checked read in `AppHost.Build`
  leaves the defaults in place. A file gone after it was accepted is refused the same way. A new
  setting needs its check in `SettingsCheck` and a row in the README (the catalog finds it by
  itself). Tests never touch the real settings: `SettingsFileFixtures` (`TestDoubles.Settings.cs`)
  gives program, user and state folders in temporary folders, and `FakeOutputFolderFacts` stands in
  for the folders' facts. Environment overrides such as
  `ElevenLabs__BaseUrl` work; one that fails the check stops the start, since it cannot be fixed
  while ClassScribe runs. Warnings are given once per process: a key in the settings, a `Logging:LogLevel` entry under
  `ClassScribe.` that names no part of ClassScribe (see the log categories above), an API version
  other than the one the build was written for, a limit of ElevenLabs' above its own, a safety stop or a
  video's limit held by the file limit (see the vendors' values below), and a
  recordings folder (`Recording:OutputFolder` or a class's `OutputFolder`) inside OneDrive, Google
  Drive for desktop or Dropbox, at a drive's root, the user folder itself, or holding `AppData` or
  `Windows` (`SettingsCheck.FolderWarnings`, over `IOutputFolderFacts`). `SettingsReloader.Snapshot` (`ISettingsStore.Snapshot`) gives the Recording, ElevenLabs (never the
  key), Transcript, Drive (no secret is ever a setting), YouTube and Logging sections as they are now, flattened; `RecorderLauncher` hands it to
  the recorder in the request file, and `RunJournal.WriteRequest` leaves the key out should it ever
  be in it. A recording that is running keeps the settings it started with. The microphone's
  settings are `Recording:Mode`, `Recording:Microphone`, `Recording:MicrophoneProcessing` (`Raw` or
  `Default`), `Recording:MicrophoneFollows` (refused when empty or when it names ClassScribe; not
  read in app mode) and `Recording:ClassAppMute` (`Visible`, `Invisible`, `Unknown`; a fact about
  the class app, so no command-line option), plus `Transcript:ShowGapsLongerThanSeconds` (0 or more).
  The camera's settings (camera-first step 11) are `Recording:CameraResolution` (`720p`; `480p`, `720p`, `1080p` in any
  case or `WxH`, checked by `CameraSize.TryParse`, the one parser `--resolution` and menu choice 7 use too; amendment 39)
  and `Recording:WakeCameraMicrophone` (false, a yes or no: the webcam hold, see "The webcam hold" below; set in the
  user's own file with `settings set`, or for one ClassScribe through its environment, `Recording__WakeCameraMicrophone=true`,
  which the window's settings snapshot carries to the recorder), and `Recording:CameraOffsets`
  (`{}`: a camera's name part to the milliseconds its picture is moved against the sound, the longest part in the
  camera's name applying, `CameraOffsets.For`; each part 1 to 200 characters without control characters or a colon, each
  figure a whole number from -1000 to 1000; checked by `SettingsCheck.CheckWholeNumberMap` before the bind, so a value that
  is no whole number is a problem in words, not a binder's exception; resolved by `TrackSetFactory` from the run's
  snapshot into the camera track's start; `Recording__CameraOffsets__OBSBOT=-60` sets one for a ClassScribe's environment,
  as the kit does for the long checks). `Recording:CameraOffsets` is a map of `SettingsCatalog` (amendment 25, as
  `Logging:LogLevel` is, its entries whole numbers and none by default): `settings set Recording:CameraOffsets:<camera> <ms>`
  sets an offset, `settings reset` takes it out, the full copy writes the map empty (`SettingsWriter.AddMissing`), and
  each problem with an offset names that entry's own key (`Found.AddAbout`), so a recording leaves out only the offsets
  the check refuses, a name part with a space included. New choices are new keys, not new values of an existing enum
  (amendment 5): an older build
  refuses a value it does not know, and a key it does not know it passes over.
  The cost line's settings are `ElevenLabs:PricePerHourUsd` (0.22) and
  `ElevenLabs:KeytermsPricePerHourUsd` (0.05), each from 0 to 100 dollars an hour, and
  `ElevenLabs:ReadAccountUsage` (true). The vendors' values (architecture slice 2, design 7.2, with
  Gulzar's answers 9 A and 10 A): `ElevenLabs:ApiVersion` (`v1`) and `Drive:ApiVersion` (`v3`), refused
  unless `v` and one to three digits (they go into every request's path) and warned about, naming
  `settings reset`, when not `ElevenLabsApi.WrittenFor` or `DriveApi.WrittenFor`;
  `ElevenLabs:MaxKeyterms` (1000; 1 to 10,000), `KeytermCharacterLimit` (50; 2 to 1,000),
  `MaxKeytermWords` (5; 1 to 50) and `LongestFileHours` (10; 1 to 24), each refused only outside its
  range and warned about above ElevenLabs' own, as ElevenLabs then refuses the whole request;
  `Drive:AuthorizationUrl` (Google's sign-in page, which `GoogleSignIn` opens; `CheckSignInPage`:
  `CheckLocalOrHttpsAddress`, and no `?` or `#`, so no parameter of the settings' can come before the
  `drive.file` scope, which stays in code);
  and `ElevenLabs:BaseUrl`, now https or http to a loopback address only, since the key travels in a
  header. `TranscriptionLimits` (`Transcription/`, built by `From(ElevenLabsOptions)`) is the file limit
  and what follows from it: `LongestRecording`, half an hour less (`ClassFolders.FindContinuable`,
  `DownloadGuard`, and with the minute of `Overrun` the workflow's refusal, `Sends`, so that the
  recording a stop held there ends, which runs a moment past it, is still sent); `SafetyStop`,
  `Recording:StopAfterHours` (0 to 24) held at `LongestRecording`; and `VideoLimit`, `YouTube:LongestVideoHours` held an hour within the
  file and never below a quarter of an hour (the monitor and `VideoBatch`'s plan). A setting bounded
  by another is warned about and held, never refused (`SettingsCheck.WarnOfTheStops`), so lowering the
  file limit never stops a command; the monitor's, the plan's, the guard's and the workflow's words are
  built from the values. `RecordingMonitor`, `ClassFolders` (`IOptionsMonitor`) and `DownloadGuard` take
  the ElevenLabs settings as an optional last argument, the defaults when not given. The half hour and
  the hour are ClassScribe's own margins, in code. The Drive section (`DriveOptions`, `Drive/`): `Drive:FolderName`
  (`ClassScribe`; not empty, at most 100 characters, no control characters; names the root folder
  only when it is first made), `Drive:ApiBaseUrl` and `Drive:OAuthBaseUrl` (Google's; https, or
  http only to a loopback address, for a stub), `Drive:ApiVersion` and `Drive:AuthorizationUrl` (above),
  `Drive:Upload` (`Everything` or `WithoutAudio`, by
  name), `Drive:ChunkMegabytes` (8; from 1 to 256 MiB) and `Drive:Enabled` (true: each class goes
  into the queue as its run starts and up after it, and a transcript made later sends its new files;
  false: nothing is queued or sent by itself, only 9 and `drive upload` upload) and `Drive:LessonVideos` (false: a
  lesson's `<base>.mp4` stays on this laptop; true sends it too, with `Drive:Upload` `Everything`). The YouTube section
  (`YouTubeOptions`, `SettingsCheck.CheckYouTube`): `YouTube:YtDlpPath` and `YouTube:BrowserPath`
  (empty finds the program; else a full path to an `.exe` that is there, environment variables
  expanded, quotes ignored: `CheckExe`), `YouTube:MayDownloadChannels` and `YouTube:CourseChannels`
  (lists of UC ids or @handles, `YouTubeOptions.IsChannel`; an entry in both is refused, and a scalar
  where a list belongs is refused by `Expected`), `YouTube:DriveUpload` (`Everything`, the default, or
  `WithoutAudio`, by name; `RecordingUpload.SendsAudio` reads it for a recording with a source file in
  place of `Drive:Upload`), `YouTube:LongestVideoHours` (4; from 0.25 to 24; above
  `ElevenLabs:LongestFileHours` less an hour it is held there, and above `Recording:StopAfterHours` the
  safety stop ends a video first: each a warning from `FindWarnings`, never a refusal, since a refused
  file would hold up every class over a setting of the videos', and the plan then names what decides)
  and `YouTube:OEmbedBaseUrl` (`https://www.youtube.com/`; https, or http only to a loopback address:
  `CheckLocalOrHttpsAddress`, which Drive's addresses use too). The profile folder, the silence that
  ends a video (2 minutes), the pause between videos (his Enter) and separate Deno and FFmpeg paths are
  fixed parts of the design, not settings.
- Google Drive sign-in (slice 1 of design `drive.md`; `GoogleSignIn`, window only, never a recorder):
  menu choice 9 (`DriveMenu`: the state from the store alone; S, and while signed in I and O) and
  `drive signin [--client "<file>"]`, `drive signout`, `drive status` (`DriveCommand`, through
  `ICommandRunner.DriveMenuAsync`, `DriveSignInAsync`, `DriveSignOutAsync`, `DriveStatusAsync`, each
  in a scope of its own). The client file (`GoogleClientFile`: `installed` only, a `web` client
  refused, at most 64 KB, never quoted) is imported and stored at once (same client id: only the
  secret is taken and the sign-in kept). The listener (`ILoopbackReceiver`,
  `HttpListenerLoopbackReceiver` on `http://127.0.0.1:<free port>/`, five tries) and the browser
  (`IBrowser`, `ShellBrowser`, which never logs the address) are seams. The address is
  `Drive:AuthorizationUrl` (Google's sign-in page, read from the run's settings; a query or fragment of
  it, which the check refuses, is left out) and carries PKCE S256, `drive.file` alone, `prompt=consent`
  and `access_type=offline`, and no other parameter; the wait is 5 minutes on the
  `TimeProvider`, Esc or Ctrl+C gives up (a cancel), any request without the state gets 400 and the
  wait goes on, `access_denied` ends it. After the exchange, `drive.file` must be granted and a
  refresh token given; the sign-in is stored first, then Drive's about is read and the root folder
  found or made (`DriveFolders`: remembered id in `%LOCALAPPDATA%\ClassScribe\drive\folders.json`,
  else `appProperties` `classscribe=root`, else made; trashed folders never count and are never
  touched); a failure there puts back what was stored before. `GoogleTokens` (one per run, over
  the process's `GoogleTokenCache`): renews when less than 2 minutes are left, one renewal at a time,
  and at once after a 401; `invalid_grant` removes the dead token (only if it is still the one
  stored) and keeps the client. Sign-out revokes, then forgets the token and keeps the client
  whatever Google answers. The sign-in's deliberate catches: `HttpListenerLoopbackReceiver` (a port
  taken meanwhile: another, up to five tries), `ShellBrowser`'s two (a browser that does not open:
  the window shows the address; the exception is not logged, as its message holds the address),
  `DpapiGoogleCredentialStore`'s unreadable cases (a file that cannot be read, a blob this account
  cannot open, one that is not the store's JSON: each counts as not signed in, with why) and its
  temporary-file delete, `DriveFolders`' remembered-id read and write and its temporary-file delete,
  `GoogleSignIn` (the revoke that fails at sign-out; the tab closed before the page), `GoogleTokens`
  and `DriveClient` (an error body that is not Google's JSON), and `DriveCommand`'s two (the
  command's answer, exit 1).
- Google Drive uploads (slice 2 of design `drive.md`; by themselves after each class, slice 3,
  below): menu choice 9 > U (the
  queue), R (a recent recording, listed with `on Drive`, `waiting`, `not uploaded`), F (a dragged
  path), L (free space, asked with Y), and `drive upload ["<path>"]`, `drive free-space "<path>"
  --yes` (`DriveUploadCommand`, through `ICommandRunner.DriveUploadAsync` and
  `DriveFreeSpaceAsync`). A path names a recording by any of its files; a folder its recordings and
  its date folders' (`ClassFolders.RecordingsByAnyFileIn`), its other folders (Slides) passed over
  silently; a folder of courses (one holding no recording, or one whose other folders hold
  recordings, as the recordings folder with a kept answer at its top) names each course in it, one
  without a recording named, `mic-check` passed over silently, and a recording's file lying at its
  top left out with a line (`... lies in ... itself, not in a recording's folder, so it does not go
  up`, with the `rebuild` command for a kept answer). `DriveUploadPass`: the recordings asked for are
  queued (manifests, only once a client is kept), unless `RecordingUpload.IsUpToDate` (done, and
  every file that goes up unchanged since its check, by size and write time; files gone, as audio
  freed, change nothing), so a done or freed recording asked for while signed out or while the lock
  is held is never said to wait; then they go up, then the rest of the queue that is due
  (`NextAttemptUtc`), or all of it for `drive upload` alone; a recording busy when the pass came to it
  is looked at once more before the pass lets go (`Taken`), so a class whose run ends meanwhile goes up
  with it, and so is one done with that was queued again after its try began (`Taken.Begin` notes
  `UploadManifest.TimesQueued`, which `IUploadManifests.Queue` counts up; `KeepWaiting`, what an upload
  writes of a recording it cannot take now, does not); what waits goes up
  `DriveUploadPass.AfterTheNextClass` ("after the next class, or with menu choice 9 > U, or
  ClassScribe drive upload") while `Drive:Enabled` is on, else `HowTheQueueGoesUp`; one pass at a time under `%LOCALAPPDATA%\ClassScribe\drive\upload.lock`
  (`DriveUploadLock`, `FileShare.None`; a pass that finds it held leaves its recordings queued and
  says `Another ClassScribe is uploading to Google Drive; ... waits and goes up after it`; never
  `recording.lock`); `StayAwake.UploadingReason` held for the pass; before each piece it waits,
  polling the run journal every 30 s, while any run records (`Google Drive: paused while "Kalam"
  records`) or, after that, transcribes or makes its lesson video (N13: `paused while "Kalam" is transcribed`); Google out of reach after retries, Drive full, a sign-in gone or a manifest that cannot
  be written (`UploadNotTrackedException`, thrown by the run's `Save` in words of its own, never taken
  for a file that could not be read) ends the pass, a file or folder Drive refuses keeps that
  recording waiting. `RecordingUpload`: busy when a live run records or transcribes it or
  `ClassFolders.IsBeingWritten` (`KeepWaiting`, `InProgress`); a folder that is not there
  (`FolderNotThere`) waits, with `LastError`, only when its drive is not there either; on a drive that
  is there it was moved or deleted by hand: done when its manifest has entries and every one is
  verified, else `UploadState.Missing` (out of the queue, `RecordingOutcome.Missing`, which
  `PassResult.AllDone` counts as nothing waiting) with a warning that says how to upload it from where
  it is now; asked for again once its folder is back, it is queued as any other. A run reads the
  manifest's `TimesQueued` at each write and keeps a queue written meanwhile by another process; at
  its end, a recording queued again since the run began stays `Waiting` (`... was put in the queue
  again while it went up, so it is looked at once more`), so a (2).md made during an upload is never
  left behind; the files are `RecordingFiles.TryParse`'s of the same base name but the
  `.upload.wav` (and with `WithoutAudio` the `.wav`s), never a PDF beside them, in the order `.md`
  copies, `.json` answers, `.marks.txt`, main `.wav` parts, `.mic.wav` parts, `.log` last; Drive's
  folder is `IGoogleDrive.FindOrCreatePathAsync(IClassFolders.DriveLevelsOf(IClassFolders.AsOnDisk(folder)))`
  (course > date folder, or an old flat folder's name; each level by remembered id in
  `folders.json` keyed by its path under the root, else by escaped name in its parent, else made;
  levels forgotten when the root changes); each file's MD5 is cached in the manifest by size and
  write time, audio is opened `FileShare.Read | Delete` (a sharing violation: it waits), text is a
  snapshot read with `ReadWrite | Delete`; a file without a checked copy is looked for in Drive's
  folder by name and adopted when its size and MD5 match; after the last piece `files.get` must
  give the same size and `md5Checksum`, else a warning with both and one new revision of itself,
  then the recording waits; a verified text file that has grown goes up as a new revision while
  `files.get` finds its copy outside the trash, and otherwise, its copy deleted or trashed on Drive,
  as a new file in the folder (the trash is never touched), and so does a verified lesson video made
  again (`TargetOfAsync`'s `lessonVideo`, with the line `<base>.mp4 was made again since its copy went
  up ...`); any other change is left as it is on
  Drive with a warning; without a manifest, a same-name text file on Drive gets a revision only when
  the local one is larger; a file gone from the laptop after its copy
  was checked stays in the manifest and counts as safe. Lines: `Google Drive: uploading <base> (n
  files, size) to ClassScribe/<course>/<date>`, `<file>: 30% (...)` per tenth of a multi-piece file,
  `<file>: already on Drive`, and `<base> is on Google Drive: My Drive > ... (n files, size, each
  checked against Drive's MD5): <link>`, which, while this process still writes the recording's log
  (`RecorderLog.NamedFile`, as a recorder after its class or a run in the window), is written and
  flushed just before the `.log` goes up, so Drive's copy holds it, and is said once. Each try clears
  `LastError` as it begins, and every write of an upload stamps `UploadManifest.UpdatedUtc` (a queue
  clears it, unless the upload goes on now, which the queue leaves as it is), and an upload held up
  between pieces, as while a class records or is transcribed, writes the manifest every
  `RecordingUpload.StillGoingOnEvery` (1 minute; `FileWatch.BeforePieceAsync`, the pending wait's timer
  going with the pause), so `IsBeingUploaded(now)` (waiting, tried, no error, written within `GoingOnWithin`, 5
  minutes) tells an upload going on now, in any process, a paused one hours into its pause included,
  and a recording queued again right after
  its upload is not taken for one; `BytesOnDrive` says how much Drive has. `ResumableUpload`: pieces of `Drive:ChunkMegabytes` MiB
  (`PieceSize` for tests), each `PieceContent` with its Content-Range streamed from the open file;
  308 confirms by its `Range`, 200/201 completes; a passing failure (Unreachable or Busy: 408, 429,
  5xx, 403 rate limits) waits as `DriveRetry` says (2, 4 ... 64 s plus up to 1 s, 8 attempts) and
  asks the session `bytes */size`; 404/410 starts a new session once; a kept session is used only
  for the same size, MD5 and target, under six days old, at Drive's own address
  (`UploadAddress.TryParse` against `Drive:ApiBaseUrl` and `Drive:ApiVersion`: scheme, host, port, and the path
  `upload/drive/<version>/files`); a piece may take
  the longer of 2 minutes and its bytes at 0.25 Mbit/s. `DriveClient` classes refusals:
  `DriveProblem.Busy` (retried), `Full` (`storageQuotaExceeded`; `uploadLimitExceeded`,
  `dailyLimitExceeded`: the recording's next automatic try a day later), else `Refused`.
  `UploadManifests` (`drive\recordings\<base>.<8 hex of SHA-256 of the folder's path in
  capitals>.json`, `UploadJson`, states by name, written atomically with three retries; one that
  cannot be read counts as none, with a warning; a write that still fails is thrown, said in words by
  its caller and logged by `Write` at Debug alone). `drive status` ends with the queue (`Waiting:
  <base> (n files, size), not tried yet` or `last try <local time>: <error>`, or `Nothing waits to
  go up.`), and 9 > U's line counts it, both through `DriveQueueReport.Queued()`, which leaves out a
  class being recorded or transcribed now, as the header and the report do. Free space
  (`LocalAudioFreer`): refused for a busy recording or an upload copy open in a
  program, or one without a manifest (with the `drive upload` command to run); every `.wav`,
  `_partN.wav`, `.mic.wav` and `_partN.mic.wav` must be verified in the manifest, its MD5 worked out
  afresh, and Drive asked now for a copy outside the trash with the same size and MD5; any failure
  keeps all of that recording's audio; then the files are deleted (nothing else, nothing on Drive),
  `FreedUtc` and the length kept in the manifest as it is then (read again, so a queue written
  meanwhile stays), and `Freed <size> on this laptop: ...` said. A lesson's camera pieces, kept for a video bound for
  Drive, are freed the same way once its `.mp4` is verified there (`FreePiecesAsync`; the video stays here too; only the
  pieces not newer than the video), and a lesson whose video is still to be made keeps everything. Menu choice 4 with a folder asks
  about the recording transcribe would take, a newer freed one included (`MenuCommand.RecordingAt`,
  as `TranscribeCommand.TranscribeFolderAsync`). The
  rest of the app reads freed audio through `IAudioOnDrive` (`Storage`, `DriveCopies` from the
  manifests: verified audio missing locally; `AllOfIt` when none is left): `ClassFolders` lists such
  a recording (`RecordedClass.OnDrive`, its length from the manifest), the list and menu choice 3
  note `audio on Drive`, choice 3 offers only R (rebuild from the saved answer), and `transcribe`
  refuses a file or folder of it, or one with part of its audio gone, saying where the audio is and
  giving the free rebuild command; `rebuild` works from the local `.json` as before. The deliberate
  catches of the uploads: `DriveRetry.RunAsync` and `ResumableUpload.SendAsync` (a passing failure is
  asked again after a wait), `UploadManifests.Write` (a held file tried again), `ReadFile` (a manifest
  that cannot be read counts as none) and `TryDelete`, `DriveUploadLock.TryTake` (as `RecordingLock`),
  `RecordingUpload`'s run (a file in use: the recording waits; one file refused or unreadable: the
  others go on), `DriveUploadPass.RunAsync` (Google out of reach, Drive full, a sign-in gone or a
  manifest that cannot be written ends the pass, with what waits) and `UploadOneAsync` (a recording
  refused waits), `LocalAudioFreer` (a
  file that cannot be deleted stays), `RecordingFolder.IsUploadCopyInUse` (the look `LocalAudioFreer` made, moved to the
  storage port in slice 3c: a sharing violation is the answer; no upload copy),
  `DriveUploadCommand.FreeSpaceAsync` and `ClassFolders.OnDiskName` (a folder that cannot be looked
  into keeps the name given).
- Google Drive by itself (slice 3 of design `drive.md`): `IDriveAutoUpload` (`DriveAutoUpload`,
  scoped: one per run, shared by the run and whoever ran it). Once Drive is set up (a client kept, or
  a store that cannot be read) and `Drive:Enabled` is on, `RecordCommand` step 3 queues the run's
  recording beside the run file (`Queue`; a queue that cannot be written is a warning, never the
  class's failure) and `End` notes the outcome (`Ended`); after a `Transcribed`, `Recorded` or
  `TranscriptFailed` outcome, `AfterRunAsync` runs one pass, `[own recording]` then the queue that is
  due; never after a cancel, `NothingRecorded`, `RecorderFailed` or a run without an outcome (Windows
  ended the session): the recording waits. It runs in the recorder as `RecorderHost` step 8, after
  the end has gone to the windows and the cost line, in the run's scope (its lines in `<base>.log`
  and `recorder.log`; the recorder lives on for the upload), and in this window in
  `RecordStarter.RecordInThisWindowAsync`, in the run's scope after the run returns or throws.
  `TranscribeCommand` (after the cost line, a file transcribed on its own too) and `RebuildCommand`
  (after `NothingCharged`) call `UploadAsync(RecordingFiles.TryParse(transcript))`: only the new files
  go up. Its pass is a `Lazy<DriveUploadPass>`, made only when one runs; a first Ctrl+C cancels it
  (`signals.RequestStop()` first); it never throws and never changes an exit code; before Drive is
  set up or with `Drive:Enabled` off it says nothing but at Debug. `DriveQueueReport` (singleton,
  store and manifests only, never Google): `HeaderLine()` ends `StartupReport.HeaderLines()`, so the
  menu's header shows `Google Drive: signed in; nothing waits to go up.`, `... 2 recordings wait to go
  up (1.4 GB). 9 > U uploads them.`, `Google Drive: uploading <base> (41%); 1 more waits.`, `Google
  Drive: not signed in; ... 9 > S signs in.` or `the sign-in cannot be read ... 9 > S signs in again`,
  with ` Uploads after each class are off (Drive:Enabled).` when off, and nothing before set-up;
  `Waiting()` gives the report at the start its line after the runs (`Google Drive: 1 recording waits
  to go up (<base>, <size>; last try 17 Oct 21:40: <error>).`, or several `in all`), with `U  upload
  it now` / `Enter  go on to the menu` (`StartupAction.Upload`, `StartupChoice.RunId` null, through
  `ICommandRunner.DriveUploadAsync(null)` once the gate passes), the sign-in advice when signed out,
  and on the command line `To upload it: <CommandLine.DriveUpload(null)>`. A recording being recorded
  or transcribed now (`RecordingUpload.BusyIn`) never counts as waiting, nor does one uploading now.
  Deliberate catches: `DriveAutoUpload.Queue` (the queue at the start) and `UploadAsync` (a cancel,
  and any failure, said with what waits). Two behaviours that are by design, so do not "fix" them
  without asking: a run that captured nothing was queued at its start and stays queued, so its folder's
  `.log` goes up with the next pass into the course > date folder and counts as waiting until then;
  and in a recorder the pass goes on to other recordings after its own, and those lines land in this
  class's local `.log` after its copy went up, so Drive's copy lacks them until that recording goes up
  again (a later transcript, rebuild or explicit upload sends the longer log as a revision).
- Google Drive against the real Google (design 5.3): Gulzar is signed in, and his classes go up by
  themselves after each one, from the hidden recorder that lives on after the window has the end, into
  the course > date tree; so the sign-in through the loopback receiver, the resumable upload in pieces
  (the 308/`Range` handling, the `Location` format, Refit's `[Body] HttpContent` with `Content-Range`
  through a real `SocketsHttpHandler`) and the recorder's reach of the network have run for real, not
  only through `FakeGoogle` and the design's local probe. Never from a test or a manual run without
  asking, and still unseen against Google: the 403 reasons `uploadLimitExceeded` and
  `dailyLimitExceeded`, which `FakeGoogle` plays by Google's pages and no real answer has shown; and
  whether Google's console and consent screens still match the README's steps, which only a new
  sign-in would tell. His class of 17 October (about 727 MB, once moved to
  `%USERPROFILE%\ClassScribe\Mantiq\2026-10-17` with its files renamed `Mantiq_2026-10-17_14-04-42Z.*`;
  his PDFs and notes beside it stay on the laptop) goes up only when he asks for it. Its `.json` was
  re-indented by his editor (tabs, CRLF, 5.2 MB) and still parses whole (39,543 words, 7,884 s), so
  `ElevenLabsAnswer` and `rebuild` must keep reading pretty-printed answers. His run file
  `20261017-140441Z-af758de8.json` in `%LOCALAPPDATA%\ClassScribe\runs` still names the old
  `Mantiq_10_17_2026` folder; it looks finished, but if the start-up report ever names it, that is why.
- YouTube, what both paths share (design `youtube.md` section 2; the `YouTube` folders of `src/ClassScribe.Core`,
  `src/ClassScribe.Application` and `src/ClassScribe.Infrastructure`): `youtube`
  and menu choice Y (`YouTubeMenu`: links pasted or a dragged list file, the course with Enter taking
  `YouTubeMenuMemory.LastCourse`, R or D, T or O, Esc back at every step; R leaves out playlist and
  `/live/` links and refuses more than 10), through `ICommandRunner.YouTubeAsync`, `YouTubeMenuAsync`
  and `YouTubeCheckAsync`, each in a scope of its own; `YouTubeCommand` sends the batch to `VideoBatch`
  or, with `--download`, to `DownloadBatch` (whose cancel is thrown, after `signals.RequestStop()`, so
  one Ctrl+C stops it). Links: `YouTubeLinks.Read` (watch with `www.`, `m.`, `music.` or none, `youtu.be`,
  `/shorts/`, `/live/` (`YouTubeLinkKind.Live`), `/playlist?list=`; channels and embedded players
  refused; `t=` kept as `StartSeconds`; `FromEmbeddedPlayer` from `embeds_referring_euri`,
  `embeds_referring_origin` or `feature=emb_...`), every link rebuilt from its ID (`Url`, `UrlAtStart`);
  `LinkList` (UTF-8 list files, `#` comments, duplicates by ID taken once, the embedded mark carried
  over). `YouTubeBatch`: `TakeLock` (`YouTubeLock`, `<RuntimeFolder>\youtube\youtube.lock`, opened
  `FileShare.Read` with the holder's process id written in it so a refusal names the window, tried three
  more times 100 ms apart; never `recording.lock`, and no class waits for it), `HasKey` (checked right
  after the lock, before anything is asked of YouTube or a window opens), `KeepAwake`,
  `AccountLineAsync`, `WatchKeys` (`BatchKeys`: Q once stops after the one in hand, Q again within
  `SecondPressWithin` (5 s) at once; `NextStep` forgets a Q of the fetching before the transcripts),
  `ClassWait` (before each video and each transcript, while the run journal lists a run that records
  and is not a video's, polling every 30 s, said once each way; it returns whether it waited) and
  `OpenCourse` (the course folder once, `Recording:OpenFolderWhenDone`, `--no-open`). `VideoIndex`
  reads every `.source.json` under the recordings folder once per batch and judges each video by ID
  (`PastKind`): a recording that holds the whole video (`PastVideo.IsWhole`: a download, or a confirmed
  end) with a transcript is left out; without one it is transcribed in this batch (a kept paid answer
  gets the `rebuild` command); anything else (no audio, or may be cut short, transcribed or not) is
  fetched again as a new recording; `--again` fetches all. `BatchTranscripts` transcribes each in a
  scope of its own (`ScopedVideoTranscriber`, `VideoTranscriptRun`: `TranscriptionRuns.Begin` writes an
  in-window `Transcribing` run record carrying the video's request, its heartbeat from
  `TranscriptionProgress`, its outcome and `Seen`, so `transcribe`, choice 3, Drive's `BusyIn`, the
  header and the start-up report see it; the recording's `.log`; `UsageReport`; then Drive's
  `UploadAsync`), looks at each recording again first (`Meanwhile`: transcribed, a kept answer, being
  written, or a live run's: not sent), stops all at ElevenLabs' 401, 403, 402 or 429, and projects the
  rest after the first. Every YouTube folder derives from `RuntimeFolder` (`YouTubeFolders`: `youtube\`
  with the lock, `converting.json`, `staging\<ID>\`, `yt-dlp-cache\`; and `video-browser\`, named only
  so that nothing else goes in it and yt-dlp is never given it), so `RuntimeFolder`'s switch keeps
  tests out of the real ones.
- YouTube, the download path (slice 1, `DownloadBatch`): the lock, the key, `DownloadedAudio.CleanUp`
  (the files `converting.json` names, with a line, and staging folders untouched for 7 days),
  `YtDlpCheck` (`ToolLocator`: `YouTube:YtDlpPath`, winget's links folder, then PATH from the registry,
  the user's before the machine's; Deno and FFmpeg with FFprobe beside yt-dlp first; then each
  tool's `--version` or `-version`, and a warning past `OldAfter`, 90 days, by yt-dlp's version, which
  is its release date; ClassScribe never updates it), the reads (`YtDlpArguments.ReadPlaylist`, a flat
  `-J` listing; `ReadVideo`, an info JSON into `staging\<ID>\`, read by `YtDlpRead`; never for a video
  done before or a link from an embedded player; paced by `BatchPace`: reads 6 s apart, downloads one
  at a time 10 to 20 s apart at random and at most 60 begun an hour; the first failure that stops the
  fetching stops the reads too), `DownloadGuard.Judge` (the first that applies: a course channel by UC
  id, `/channel/` address or @handle; a sign-in by availability or `age_limit`; `is_live`,
  `is_upcoming`, `post_live`; no length; over `ElevenLabs:LongestFileHours` less half an hour, 9.5 h by
  default, its words built from it (`TranscriptionLimits`); `ChannelUnknown` while `CourseChannels` names any
  and the read names no channel; `MayDownloadChannels`; public; else a word, or `ready (--allow-unlisted)`
  for one `AllowUnlistedTakes`: unlisted, of a known channel), the plan (`DownloadPlan.Lines`,
  `ReadyLines` with `Waiting for your word`, `FetchLines`; `PlanRoom` with `ClassReserve`, room for a
  class of `KeptForAClass` = `Room.SpaceWarningBelow` (3 h) by `Room.For`,
  checked on the recordings drive and `RuntimeFolder`'s; the Drive line from `IDriveRoom`), the
  confirmation (`ConfirmAsync`: in the menu a word per video, then the lines again and a room check for
  what is then ready, then `YES` typed in full; `--yes`; `--plan` returns 0 before), the downloads
  (`FetchAsync`: after `ClassWait`; `YtDlpArguments.Download`, from the info file while it is under
  `ReadKeptFor` (5 h), else after a fresh read; a 403 reads again once; `-f ba[ext=m4a]/ba[acodec^=mp4a]/ba/b
  -x --audio-format wav --postprocessor-args "ExtractAudio+ffmpeg_o:-ac 1"`, so YouTube's own rate, mono;
  the WAV taken only from `done.txt`; the recording made only now, the batch's first by
  `CreateRecording` and the rest by `CreateRecordingIn`; its `.log` from `RecorderLog.KeepFromNow`;
  the source file written `Downloading` then `Downloaded`; `DownloadedAudio.Convert`: `converting.json`,
  NAudio's reader, mono 16-bit at the source's rate through `WavSegmentWriter` into `.partial` parts
  split at `Recording:MaxSegmentMegabytes`, within `LengthTolerance` (2 s) of the video or nothing kept
  (`IncompleteAudioException`), then renamed; the staging deleted), `YtDlpErrors.Classify` (bot check,
  rate limit, setup, exit code 2 and Windows refusing to start yt-dlp stop the fetching; format, sign-in,
  region, gone and network go on, three network failures in a row stop; any sentence naming cookies is
  dropped), the transcripts, Drive (a transcribed video's recording goes up in its transcript's scope;
  the rest is queued, and with `--no-transcribe` one pass runs), and `Summarize` (a video that can be
  watched but may not be or was not downloaded gets `CommandLine.YouTube`, the record path's command).
  Tools run through `IToolRunner` (`ProcessToolRunner` on `IProcessCreationApi`, never
  `System.Diagnostics.Process`): `ProcessLaunch` gains `Environment` (`ChildEnvironment.WithoutTheKey`,
  no `ELEVENLABS_API_KEY` or `ElevenLabs__` variable, plus `PYTHONIOENCODING=utf-8`), `Handles` (the
  three pipe ends, the only handles inherited, through `STARTUPINFOEX` and a handle list) and `Job`
  (`ToolJob`, kill-on-close, one per tool run: created suspended, assigned, resumed; a cancel
  terminates the job; a closed window ends it); `WindowsCommandLine` quotes each argument;
  `YtDlpArguments` builds every list (the fixed set first: `--ignore-config --no-cookies
  --no-cookies-from-browser --no-mark-watched --no-colors --sleep-requests 1 --js-runtimes deno:<path>
  --ffmpeg-location <folder> --cache-dir <cache>`; the link last, rebuilt from its ID;
  `--load-info-json` only inside a video's staging folder) and checks it with `ProblemWith` before
  yt-dlp ever sees it.
- YouTube, recording as it plays (slice 2, `VideoBatch`, attended): the lock, `DownloadedAudio.CleanUp`,
  the key, `BrowserLocator.Find` (`YouTube:BrowserPath` alone when set; else App Paths under HKLM then
  HKCU; else Program Files, Program Files (x86), `%LOCALAPPDATA%`), the titles (`OEmbedTitles`), the
  index, the plan (`VideoPlan`: the worst case at the shorter of `YouTube:LongestVideoHours` and
  `Recording:StopAfterHours`, each held by the file limit as the monitor holds it, named), Y in the menu (which opens the first at once), then per video:
  `ClassWait`; the `Next: ... Enter opens it; S stops ...` prompt (Enter, S or Esc, Ctrl+C twice, never
  a window without his key; `--yes` asks it before the first too); `ClassWait` again after Enter;
  `VideoWindow.OpenAsync` (`IProcessStarter.StartBrowser` with `VideoWindow.Arguments`:
  `--user-data-dir=<RuntimeFolder>\video-browser --no-first-run --no-default-browser-check
  --app=<link at its start>`, never an autoplay switch, working folder `<RuntimeFolder>\youtube`; the
  hand-off check: the started process must not have exited within 3 s, or Chrome handed the video to a
  window left open, `HandedOff`, and Enter tries again); `RecordAsync` (a `RecordRequest` with
  `VideoRequest`, `RecordingMode.Class`, `ClassSource.App("chrome")`, `Transcribe` false, `Continue`
  `New`, through `IRunStarter.RecordAsync` (`VideoBatch` takes the run starter alone, which the runner is) with
  `StartOrigin.VideoBatch`, shown here as the run's
  owner; its run found in the journal by batch id and position, since the starter may give a new run
  id; a start refused because a class records is waited for and asked again); `VideoWindow.CloseAsync`
  (`WM_CLOSE` through `IVideoWindows`, never a kill, 15 s); two windows or runs in a row that failed
  stop the videos (a run that played nothing does not count); Esc in a video's view leaves it recording
  in the background (`RecordedVideo.Left`) and opens no more. Then the transcripts (after
  `signals.RequestStop()`, so one Ctrl+C cancels an upload) and the summary (`For the rest:` after
  `VideoLimit` or `SafetyStop`, from `StartSeconds` plus the length). The run: `RecordCommand` with the
  scoped `VideoPlayback` and `IVideoYield` listens for the yield from its very start (heard before the
  run file exists, it still ends as `ClassStarted`); `OpenFiles` puts it in `Video.Folder` with
  `CreateRecordingIn`; step 3b writes the source file (`VideoEnds.NotFinished`) and fails closed with
  `VideoWindowClosedException` when `VideoPlayback.Begin` finds the window gone; step 7b writes the
  source file's end (`VideoEnds.Of(EndedBy)`: closed, moved on and Q confirmed, the rest not) and
  posts the close, never waiting. `CaptureChooser` given a video window (`ChooseVideo`) records
  `process:<browser pid>`, its tree included, from before play, with the loudest session of
  `VideoPlayback.Tree()` (`IProcessFamily` over `IProcessTree`, by pid and start time) as its level,
  and nothing once the window is gone, never another Chrome by name; a class's `--app chrome` leaves
  out `VideoPlayback.VideoWindowsProcesses()` (live video windows from the run files, read every
  10 s). `RecordingMonitor` hands a video's run to `VideoEndWatch` before the safety stop (1 window
  gone, `VideoClosed`; 2 another title for two looks after one stood 5 s with sound, `VideoEnded`; 3
  two minutes of silence after sound, from the last sound, freeze and pause, never while paused,
  `VideoSilent`, warned at the shorter of `Recording:SilenceWarningSeconds` and 60 s in place of the
  usual warning; 4 nothing heard in 5 minutes, `NothingPlayed`, with `PressPlay` as a Notice from 30
  s; 5 `YouTube:LongestVideoHours` of recording or running, held an hour within `ElevenLabs:LongestFileHours`
  (`TranscriptionLimits.VideoLimit`, its line naming why when held), `VideoLimit`), from `RecordingStatus.HeardSound`
  and `TrackSnapshot.HeardSound`, and skips the Bluetooth watch for it. `VideoPlayback.PlayedTitle` (the
  window's title once it stood 5 s with sound) names the source file and the transcript over
  oEmbed's; `VideoTitles.Plain` takes control characters out of every word from YouTube, and `Clean`
  takes `(N) ` and ` - YouTube` off a window's title.
- A class comes first (design 2.5): `RecordStarter.RunAsync`, given a start without a video while a
  video's run records, from any origin, runs `YieldAsync`: it asks through `IVideoYield` (a run still
  `Starting` is asked again at each look until it listens; one that records and does not listen is
  explained at once), says `Stopping the YouTube video "..." so that "..." can record; what it recorded
  is kept.` once asked, waits up to `YieldPatience` (30 s) polling the journal every `YieldPoll`
  (250 ms), then starts the class as it would have; otherwise `Explain` and the Q-twice advice.
  `Winner` never shows a video's run, nor any run to a video's start; `DecideAsync` gives a video `New`
  without a question; `AfterLoss` never continues a video (its audio is kept, the run marked seen);
  `AttachCommand.ContinuationOf` is null for one; `StartupReport.CanContinue` is false for one;
  `ClassFolders.FindContinuable` passes over recordings with a source file; `RecordStarter.Named` and
  `NamedFirst` name a video's run as `the YouTube video "<title>"` in `Explain`, the menu header and the
  report at the start. The batch waits for a class before each video and transcript (`ClassWait`), and
  a download batch keeps room for a 3-hour class.
- Still to be checked with Gulzar, each asked first, since each reaches YouTube and each transcript is
  billed (design 7.4): `winget install --source winget --id yt-dlp.yt-dlp` in a new terminal, then
  `youtube check`, and whether Bitdefender flags yt-dlp.exe; a first download of one short video he
  uploaded (that `ExtractAudio+ffmpeg_o:-ac 1` reaches FFmpeg and the `.wav` keeps 44.1 or 48 kHz, how
  a stale info JSON fails after about 6 hours, the transcript's heading and links, Drive with the
  audio); a first recording of one short public video, ideally his own (the launch not handed to his
  everyday Chrome, the hand-off check with a window left open, the title read and cleaned, the first
  second recorded with the capture on the root (if a child's stream is missed, build the fallback:
  target the playing session inside `VideoPlayback.Tree()`), the end by Autoplay and by closing the
  window, `WM_CLOSE` closing it cleanly, the level with the speakers turned down and muted, the Premium
  sign-in in `video-browser`); a class started from the menu while a video records (the yield); a
  batch of three from a list file each way; then a publish to `%LOCALAPPDATA%\Programs\ClassScribe`
  with a renewed Bitdefender exception (Bitdefender may notice ClassScribe starting Chrome). Left for
  later, only if use calls for it: the media-controls probe for paused against ended, titled date
  folders, and automated playback (Gulzar chose attended).
- Still to be checked with Gulzar on 0.5.0, hands on, each asked first (the 0.5.0 review's section 5,
  `release-0.5.0-review.md` beside the build plan, written on 29 October: ask which have run since and
  strike those). The camera probe's evening 2 (its steps 8, 4, 5 and 16: the wake, and how 0.5.0 holds
  the OBSBOT shared beside Chrome) and evening 3 (steps 17 to 21), the latter before his first lesson
  with `--camera`. The kit's dry runs on a class's path (`-Mic OBSBOT`, the default now: V off and on,
  the OBSBOT unplugged and replugged once, the microphone reopening within 5 s of the start) and the
  OBSBOT pair with Auto Sleep on (`-Follows none`: the one `only exact zeros` warning and the Notice,
  then with `-Setting "Recording:WakeCameraMicrophone=true"` the hold's line, the light on through P
  and V, `Let go of the picture` at the stop, Windows' Camera app still showing the picture meanwhile).
  The installed 0.5.0 (`--version` `0.5.0+5daac45`, the template's `"MicrophoneFollows": "auto"` and
  `"CameraOffsets": {}`, `ClassScribe.previous` holding the old file) and its settings from the
  installed exe (the menu's `Settings:` line with no ERROR, WARNING or older-default line; `settings
  list`, `settings get ElevenLabs:ApiKey` refused with exit 2, `settings path`, `class list`, with no
  `appsettings.json` made in `%APPDATA%\ClassScribe` and no `settings-home.txt` in
  `%LOCALAPPDATA%\ClassScribe`; `devices` listing the OBSBOT; `camera-check "OBSBOT"` with Y and
  Bitdefender's Webcam Protection allowed). The first class on 0.5.0 (the 0.5.0 build line, the OBSBOT
  in the microphone lines, no `which the check refuses` or `Nothing of ... could be taken` warning, the
  recording in `Mantiq\<date>`, then `Transcript saved to`, the cost line, the glossary counted, `is on
  Google Drive`, no lesson-video line, the recorder ending after the upload). Later: a Zoom test
  meeting of his own on the installed build before the Zoom app is used in a class (`devices` with
  `Could not be read now:` lines and exit 0, and no `"chrome" has closed` warning in a recording beside
  it); before his first lesson with `--camera`, `camera-check` on the built-in camera and a short lesson
  with each camera (claps at the start and the end, P twice, the OBSBOT unplugged ten seconds, Q twice,
  the `.mp4` played in Media Player and VLC with the claps in step, `Recording:CameraOffsets` set if
  not), Ctrl+C once while a video is made, a record-only lesson continued with C, once a video over
  4 GiB, the 2.5-hour clock-and-clap recording, and no `--camera-process` left after Q twice; and
  before `Drive:LessonVideos` goes on, the remade video's revision kept for ever (his answer 4 b, not
  built yet) and one real video checked for fast start (review 3.19).
- The camera's open (`Camera/CameraRoute.cs`, following `ROOT-CAUSE.md` beside the camera probe, finding 7 of 21
  October): `CameraRoute.Open` makes the MediaCapture, registers `Failed`, finds the camera's source group by its id (never
  left out: a camera Windows no longer lists is an error, never Windows' default camera), calls InitializeAsync with
  `SharedReadOnly` and video only, and only after it has completed registers the events that only report (stream state,
  exclusive control; each optional, a refusal recorded and passed over) and reads `FrameSources`. Before InitializeAsync
  those are refused with 0xC00D36B6 MF_E_NOT_INITIALIZED, which is what every probe open of 21 October hit. Then the frame
  reader: no format set (a shared open cannot set one; `SetFormatAsync` is noted as not called), nothing converted for
  the hold or NV12 at the picture's size for the check, `Realtime`, each frame taken and released unread, and
  `StartAsync`, whose status must be `Success`. There is no exclusive mode anywhere: the camera is only ever shared
  (Gulzar's decision; his face sometimes shows in Zoom). Every call goes through `CameraCallLog` (name, time, OK or the
  HRESULT with its name from `CameraHresults`, the probe's checked table, and Windows' words), so a failure names its call;
  `CameraRoute.Close` lets the camera go, every call optional. `ICameraCapture` is the seam, one member a step;
  `Camera/Windows/WindowsCameraCapture.cs` is the real MediaCapture (no memory preference, or CPU memory for a record job,
  `InitializeShared(cpuFrames)`), made by `WindowsCameraCaptureFactory`. A record job opens with `onPixels`: CPU memory
  (`CameraRoute.InitializeForPixels`), NV12 at the picture's size, and `AddFrameReader` in place of `AddFrameArrived`
  (`CameraRoute.FrameArrivedForPixels`), each frame handed over as an `ICameraFrame` (its SystemRelativeTime, its size,
  `CopyNv12To`) while Windows holds it: `WindowsCameraFrame` copies the SoftwareBitmap's two planes row by row, at their own
  strides, through `IMemoryBufferByteAccess` on the CsWinRT object (`MemoryBufferBytes`, without unsafe code). `CameraThread` is the camera process's own STA thread: every call on the route runs
  there, each waited for at most the probe's 30 s (`CameraHost.CallLimit`), after which the thread is abandoned and the
  job ends. Fakes: `FakeCameraCapture` (refuses what Windows refuses before InitializeAsync, with Windows' exception and
  code; any step can fail, or get `Stuck` until a gate is set; `FramesOnStart`) and `FakeCameraCaptureFactory`; tests
  `CameraRouteTests` (the order of 21 October fails on the fake as every open did), `CameraThreadTests` (STA, the limit
  on the manual clock, every later call refused unqueued) and `WindowsCameraCaptureTests` (a camera id Windows does not
  list throws in `FindSourceGroup`, never a fallback to another group).
- The camera process (teach-video 6.2, amendment 24; its hold, check and record jobs open the camera, only shared):
  `ClassScribe.exe --camera-process <job id>` (under dotnet, `dotnet ClassScribe.dll --camera-process <job id>`), taken
  by `Program` before anything touches the console, as `--recorder` is, and only for exactly the switch and an id
  `RunIds.IsWellFormed` takes (`CameraProtocol.IsCameraProcessStart`; `RecorderHost.IsRecorderStart` now checks its id the
  same way), so anything else is the command line's mistake. `CameraHost` reads its job, the first line on its standard
  input, and speaks the lines of `CameraProtocol` (`Camera/CameraProtocol.cs`: the job, `CameraJob` with `CameraJobKind`
  Record, Finish, Check or Hold (the wake job renamed); to it `clock`, `off`, `on`, `stop` (`CameraStop`), `cancel`; from it
  `opened`, `status`, `problem`, `piece`, `progress`, `made`, `failed`, `log`; `CameraJson`, strict as the link's) on its
  standard output, as lines of UTF-8 JSON through `LineChannel` over a `JoinedStream` of the two pipes. Version 2 (slice 7)
  defined the `clock` line's body (`ClockState`), the empty body of `off`, `on` and `cancel` (`CameraEmpty`,
  `CameraLines.SendSignalAsync`), `CameraJob.SoftwareEncoder`, `CameraStatus.FramesPerSecond` and `Capturing` (defaulted),
  and `CameraProblemKind.NoPicture`; version 3 (slice 8) added `CameraJob.KeepPieces` (defaulted) for the finish job. Four
  jobs are taken, each on a `CameraThread`: the record job (`CameraRecordJob`, "The lesson's picture" below), the finish
  job (`CameraFinishJob`, "The lesson's video" below), and in `Camera/CameraJobs.cs` the hold (`CameraHoldJob`: the camera opened shared, nothing
  converted; a `status` at the first frame of each open and once a second, with the frames and the seconds since the last;
  a refused open said once per cause as a `problem` through `CameraProblems.ForFailedOpen` (E_ACCESSDENIED as privacy or a
  security program, a sharing violation or busy as another app, a camera not listed or not connected) and tried again
  every 5 s; a picture stopped for 3 s, or no first frame in 10 s, let go and opened again 5 s later; the starter's `stop`,
  `cancel` or closed end lets the camera go and ends it with 0; a starter found gone on a send ends it too) and the check
  (`CameraCheckJob`: the camera opened at the job's size, each call sent as a `log` line, a failed required call at
  Warning, `opened`, then a `status` with the frames over `CameraHost.CheckFor` (5 s), let go; 0 with frames, else `failed`
  and 1). A hold, check or record job naming no camera is not taken, nor a finish job naming no recording (`failed`, 4); no job within `JobPatience` (30 s), a first line that is no
  job, a job without a body or with one that is not an object, or a job of another `CameraProtocol.Version`, is not taken
  either, with why in `failed`; it never throws. Its exit codes are internal: 0 done, 1 failed, 4 a job it cannot take,
  130 cancelled. `ICameraProcessStarter` (`CameraProcessStarter`, registered in `CameraServices`, used by the webcam hold,
  the camera check and a lesson's camera track) starts it through `PipedProcess` (`Desktop/Windows/PipedProcess.cs`,
  factored out of `ProcessToolRunner`, which now starts its tools through it): CreateProcessW, no window, three pipes and no
  other handle, `ChildEnvironment.WithoutTheKey`, the working folder `%LOCALAPPDATA%\ClassScribe`, and a kill-on-close
  `ToolJob` made before it runs, the job and the pipes let go again when Windows refuses or the start throws
  (`PipedProcessTests`); its standard error goes to the log at Debug under the category `Camera`; `ThisProgram.CommandFor`
  gives this program's command line to it and to `ProcessStarter` (the record `ThisProgram` is also the `IThisProgram` the
  use cases read this program's path, folder, process id and start from, since slice 3b4). `ICameraProcess` gives the channel, the process id, the
  exit code and `Kill`; `CameraLines` sends the job and the stop and reads each line as its type (a known type whose body
  is not an object is a `JsonException`, never another exception). The real start refuses in
  a test (`DeviceGuards`, naming `FakeCameraProcess`), and Windows' tool start refuses any program but cmd.exe there too.
  Fakes: `FakeCameraProcess` (the lines over a `DuplexStreamPair`; `ProcessStream` lets the real `CameraHost` run over it),
  `FakeCameraProcessStarter` (thread-safe; `PlayEach` plays each process with a `PlayedCameraProcess`, which reads its
  job, sends status and problem lines and ends at the stop; `Run` runs, say, the real host per process; `StartFailure`)
  and `CameraLinesSeen` (a starter's reader of every line); tests `CameraHostTests`, `CameraJobTests` (the real host over
  the fake capture on the manual clock, stuck calls included: the hold's `failed` and exit 1, the check naming the call)
  and `CameraProcessStarterTests`.
- The lesson's picture (teach-video 6.5 to 6.14, slice 7; camera-first step 12; shared only, as every open): a lesson in
  person with `SourcePlan.Camera` records the sound exactly as without one, and its camera's picture into pieces on the
  same clock. `ClockState` (`Audio/Timeline/ClockState.cs`) is the clock's whole state, immutable: offset, origin, the
  exclusions (`ClockExclusion`), the open pause and a version that grows with every change; `RecordingClock.Snapshot()`
  hands one out under the clock's lock, and `Position` and `IsExcluded` are the clock's own code moved into it
  (`PositionOf`, `IsExcludedIn`, `Covered`), which the clock calls under its lock, so both processes place by one
  implementation (`ClockStateTests`: random sequences, earlier snapshots unchanged, the JSON round trip). In the recorder,
  `TrackSetFactory` gives a lesson's `TrackSet` a camera starter (its `ICameraRecorder`, optional) with the run's
  `Recording:CameraOffsets`; `TrackSet.Start` starts the `CameraTrack` once the sound records (a track that cannot start is
  an error line, the sound records on); `Pause`, `Resume` and `OnFrozen` (after the cut) reach it; `Stop` tells the camera
  the end first (`ICameraTrack.EndAt`, taking no lock: the `end` line, after which the job drops every frame placed beyond
  it), then finishes the sound (stopped, padded to the end, flushed) and then waits for the camera's stop at that same end,
  at most `TrackSet.CameraStopLimit` (25 s, on the factory's `TimeProvider`; a failing or late stop is an error line, and
  `Dispose` waits as long at most); `TrackSet` implements `ICameraRecording` (`Camera`, `LetGoOfTheCamera`). No webcam hold runs beside a
  picture (`WakeCameraMicrophone` is passed off for a plan with a camera; the webcam is still found for the zeros rules).
  `CameraTrack` (`Camera/CameraTrack.cs`) pins the camera by `Cameras.Choose` and its id (never a stand-in; not connected is
  said and looked for every 5 s), resolves the offset (`CameraOffsets.For`), starts a camera process with a record job
  (the first piece from the run's offset, a later one from the clock's position then), sends the clock's state at once and
  whenever its version changed, from its own loop every 250 ms or at once when nudged, and `off`/`on` for P after the clock's
  line; `Nudge`, `Pause` and `Resume` take no lock (a flag and a task completed with its continuation run elsewhere), and the
  track takes no session's gate, only the clock's lock inside `Snapshot()` and its own. Its stop sends the clock's last line
  and `stop` with the end, waits for the process at most `StopWait` (15 s), then kills it. Every line to the process goes
  within `SendLimit` (5 s), else the process is killed as stuck, which breaks its pipe, and the send fails as an
  `IOException` (a synchronous pipe's write cannot be cancelled); letting a process go waits at most `DisposeWait` (5 s),
  and `StartedCameraProcess.DisposeAsync` waits for its standard error 5 s at most. A process that ends by itself, or
  does not start, is started again `RestartAfter` (2 s) later (`ReadAsync` holds `_nextStart` at its maximum while the
  ended process is let go, so no look starts one early; `LetGoOfAsync` never throws: its limit and any other failure of the
  let-go are deliberate catches, the second at Debug, so that a let-go that fails never leaves the camera unstarted after an
  end, nor a start's failure uncounted, with the next look starting another process at once, release 0.5.0 review 3.13 and
  the repair of its fix), with the software encoder after an `Encoder` problem; the
  `MostFailures`th (3) within `FailuresWithin` (10 min) gives the picture up (`GivenUpText`). A restart's piece begins at
  the clock's position now, or, when a piece of that name is there already (two ends in one pause: the clock stands still),
  at the next free name, a millisecond on each time (`NextFreePieceStart`, review 3.12; it asks `IFileProbe` whether the
  piece is there, never the disk itself, since `CameraTrack` is Application's), since the camera process refuses a
  piece that is there. Its `State`
  (`CameraTrackState`) carries the frames, the rate, capturing, dark and a `CameraConcern` in the run's words with the
  camera's name (lost, no picture as Bitdefender's block, frames that cannot be written, refused, in use, encoder or disk,
  restarting, not connected, given up as Alerts; dark as a Notice), cleared when frames come again. Its start line gives the
  camera's own frame rate as it runs (`CameraOpened.FramesPerSecond`, read from the format's words by `CameraFormats`, up to
  30; 0 when Windows does not say, and the line then says `at up to 30 frames a second`). The run: `MonitorContext.Camera`; `RecordingMonitor`
  makes the concern a condition (only a picture given up while paused; a lost camera whose microphone shares its container
  adds that its microphone may be silent too), adds `; camera 1280x720, 29.9 frames a second, 3 dropped` to the status
  line, and counts the picture twice in the room (`CameraBytesPerHour`): with room for less than 30 minutes of sound and
  picture, `MonitorTick.LetGoOfTheCamera` and the loop lets it go (the words are not among the tick's lines: the track's
  `GiveUp` says them, once). `RecorderState.Camera` (`CameraState`, a last field with
  a default) gives the title `CAM WAITING` and `CAM DARK` while recording. In the camera process, `CameraRecordJob`
  (`Camera/CameraRecordJob.cs`) refuses a piece already there (never written over), makes the writer (hardware unless the
  job asks for software; a refused hardware start falls back to software once, with a warning line), opens the camera
  shared with CPU frames, and queues each frame (`FrameQueue`, `Camera/CameraPieces.cs`: 24 pooled buffers, each frame
  waiting `Hold` 0.5 s so a pause's clock line arrives before the frames it leaves out, more than `RingLimit` 15 letting the
  oldest go early); every `LookEvery` (100 ms) the due frames are placed by the latest clock line (`FramePlacer`, pure:
  before the origin, excluded, before the piece, at or before the last, under `ShortestGap` 25 ms after it, or after the
  end, dropped; else written at the clock's position plus the offset less the piece's start; no stamp, its arrival). Lost
  for `StoppedAfter` 3 s: a black frame right after the last frame (never at now's position, which a cut not yet known
  could put beyond the frames after), let go, `Lost`, opened again every `RetryEvery` 2 s into the same piece; no first
  frame in `FirstFrameLimit` 5 s: `NoPicture`; black for `DarkAfter` 10 s: `Dark` once a stretch; a status line a second.
  Only frames that can be written count, for the watch and the status's capturing (`FrameQueue.Offer(..., out
  FrameRefusal)`): frames of another size, or without NV12 pixels that can be read, are said as `Unwritable` with what comes
  (`its frames come at 1920x1080, not the 1280x720 asked for`) in place of `NoPicture` or `Lost`. The `end` line keeps every
  frame placed after the sound's end out of the piece before the stop comes.
  The stop places every waiting frame, lets the last last to the end and sends `piece`, exit 0; a cancel or the starter's
  end closing finishes the piece at its last frame, 130; a failed write finishes it as far as it goes, `problem` (`Disk` or
  `Encoder`) and `failed`, 1; a stuck call, 1. `PieceSettings` fixes the encoding: the picture's size, Main profile, the
  level from its macroblocks at 30 frames a second (3.1 at 480p as at 720p, which the probe's self-check found when Windows
  refused 3.0 at 848x480; 4.0 at 1080p), no B-frames, a keyframe every 30 frames, constant bitrate by the height's band
  (2.5, 5, 8 Mbit/s) scaled by the pixels. `MediaFoundationPieceWriter` (`Camera/Windows`) writes the piece with Media
  Foundation's sink writer through NAudio's declarations (ClassScribe's own `VideoGuids`), fragmented MP4
  (MFTranscodeContainerType_FMPEG4), the encoder's settings passed as the sink writer's encoding parameters (no ICodecAPI
  declaration), over a file ClassScribe makes itself with others allowed to read (`FileStreamAsIStream` into
  `MFCreateMFByteStreamOnStream`), the black frame at 0 first and each sample lasting until the next. Fakes:
  `FakeCameraFrame` (`readable: false` for one whose pixels cannot be read) and `FakeCameraCapture.Deliver(stamp,
  brightness)` or `DeliverFrame(frame)`, `FakePieceWriter(Factory)` (`Samples` as
  `WrittenSample`, `FinishedAt`, `FailWrite`, `HardwareRefuses`), `FakeCameraTrack` (`EndAt`, `SoundBeingWrittenAtEnd`,
  `StopHangs`) and `FakeCameraRecorder`, `FakeCameraRecording`, `PlayedCameraProcess` (`Received`, `Clocks`, `End`, `Stop`,
  `HangOnStop`, `OpenedAsync`, `RecordStatusAsync`; a record job's stop answered with its piece through
  `FakeCameraProcess.EndAsync`, which closes its own end so the starter reads its last lines), `FakeCameraProcess.Exited`,
  `InputStuck` (the starter's writes hang, cancellation passed over, until the process ends), `DisposeHangs` and
  `DisposeFailure` (letting it go throws), and `FakeCameraProcessStarter.OnStart`, which shapes each process before its
  starter sees it, so that those hold from the first line on. Tests:
  `CameraPiecesTests`, `CameraRecordJobTests`
  (the real host over the fakes on the manual clock), `MediaFoundationPieceWriterTests` (WindowsMedia: generated frames
  encoded with the software encoder into a temporary folder and read back, an unfinished piece readable, a piece being
  written holding its recording, a keyframe at least every 30 frames in the Main profile; no device), `CameraTrackTests`,
  `TrackSetCameraTests`, `RecordingMonitorCameraTests`, `RecordingLoopCameraTests` (the loop lets the camera go with too
  little room), `WindowsCameraCaptureTests` (the open's settings: shared and read only, never exclusive), `TitleTextCameraTests`,
  `CameraOffsetsTests`, `CameraFilesTests`, `CameraSoakTests` (three simulated minutes through the
  real track set, camera recorder and camera host: each mark's bright frame where its click is in the sound, through a
  pause, a cut sleep and a lost camera; and a stop whose sound takes two seconds, the piece still ending with the sound) and
  `EndToEndTests.A_lesson_with_the_camera_records_its_picture_beside_the_sound`.
- The lesson's video (teach-video slice 8 as camera-first adapts it; build-plan step 13, amendments 5, 47 and 53, answers
  N13, N18 and N19): after the stop, a lesson with the camera's pieces is joined with its sound into `<base>.mp4`
  (`RecordingFiles.LessonVideo`), written as `<base>.mp4.partial` (`LessonVideoPartial`), checked, then renamed. The plan
  is pure (`Camera/LessonVideo.cs`): `VideoSourceFacts` (size, the H.264 sequence header, length) per piece, an older
  `<base>.mp4` first when the first piece starts after 0 (a continued lesson), `LessonVideoPlan.Make` slicing each source
  up to the next one's start and the last to the sound's end; any two sources whose sequence headers or sizes differ make
  the whole picture encoded again (`EncodeAgain`, `WhyEncodeAgain`, at the newest piece's size), else the picture is passed
  through as it is; `FramesToCheck` is the first frame, the frames either side of every join (`BeforeAJoin` 33 ms) and the
  last, and `LessonVideoCheck` allows `Tolerance` (100 ms) on the length. The media work is behind `ILessonVideoMedia`
  (`Describe`, `Write`, `Check`); `MediaFoundationLessonVideo` (`Camera/Windows`) is the real one: a plain MP4
  (MFTranscodeContainerType_MPEG4, the moov after the mdat, since Windows passes over MOOV_BEFORE_MDAT), passthrough with
  the input type as the output type and only IDR pictures kept as clean points (`H264Pictures.HoldsIdr`; the software
  encoder marks I pictures that are not IDR, and a seek to one decodes nothing), or a re-encode through the source
  reader's video processing to NV12 and the software encoder with `PieceSettings`; the sound is the lesson's mix
  (`AudioPreparer.OpenMix` as `MixedSound`, the microphone left out when silent, as at upload) at 48 kHz as AAC-LC mono
  128 kbit/s, interleaved by time; the check seeks to each frame to check and decodes it, and reads the length. In the
  camera process, `CameraFinishJob` refuses a recording without its `.wav` here or with a `.mp4.partial` being written,
  deletes a stale partial with a line, leaves an unreadable piece out with a warning (and keeps it after the video: only the
  pieces it read are deleted), fails with every piece kept when Windows cannot open a source now (`CouldNotOpenNow`: an
  IOException, UnauthorizedAccessException or FACILITY_WIN32 COM error) or an older video cannot be read, leaves out a piece
  older than a readable older video and beginning before its end (which holds its picture: a leftover an earlier making
  could not delete, or one kept for Drive; a piece beginning at or past the older video's `Facts.Length` is fresh whatever
  its write time says) when the older video's picture is used before the first newer piece, with a line, and fails with the older
  video kept when no newer piece is left ("holds the picture of every camera piece here already", review 3.2), logs the plan, sends `progress`
  at each new percent, and the same percent again every `ProgressEvery` (10 s, at most 98 while writing, 99 while checking)
  only while its work has moved since the last line (the writer's ten-thousandths, the check's frames decoded through
  `ILessonVideoMedia.Check`'s progress), so that a Media Foundation call that never returns goes quiet and the maker's
  `ProgressLimit` ends it (review 3.3), fails with the pieces kept when the mix skipped a damaged `.wav` part
  (`MixedSound.Skipped`: the sound written would be shorter than the lesson's, which the check cannot tell, review 3.14), and on a pass renames
  the partial (`PutInPlaceAsync`: `RenameTries` 3, a second apart, while a player or scanner holds a file open; then failed
  with why, the checked partial kept for the next making to delete, the older video and the pieces as they were) and deals
  with the pieces: kept when the job's `KeepPieces` says the video goes to Drive (a line), else
  deleted with a line; a failure deletes the partial and keeps the pieces, with the free `finish-video` command; a cancel
  is 130. In the window or recorder, `LessonVideoMaker` (transient, `CameraServices`) starts the camera process with a
  finish job and gives a `LessonVideoResult` (`Made`, `Failed`, `Cancelled`; ended after `ProgressLimit` 2 min without a
  line, "made no headway on the video for 2 minutes, so it was ended as stuck", `CancelWait` 15 s, `SendLimit` 5 s,
  `DisposeWait` 5 s) and says the end (`MadeLine`: watch it before publishing).
  `RecordCommand` sets the run file to `Transcribing` at a camera run's stop (the state now covers the video's making),
  starts the video beside the transcription (`StartTheLessonVideo`; no pieces is a warning) with
  `LessonVideoDrive.KeepsPieces`, and waits for it on every end (`WaitForTheLessonVideoAsync`: once only the video
  remains, record-only from the stop or the transcript saved, it says `RecordCommand.OnlyTheVideoRemains` and publishes
  the maker's progress as `RecorderState.VideoPercent`, a last field with a default, which `TitleText` shows as `MAKING
  VIDEO 40%` with a green bar in place of TRANSCRIBING, review 3.17; and it catches whatever the maker's task throws,
  which the maker never does by design, says the video was not made with the `finish-video` command, and lets the run end
  as its transcript did, never as TranscriptFailed, review 3.4); a remake, at a continued lesson's stop or by
  `finish-video`, is left for later with `LessonVideoDrive.UploadGoingOnLine` while an older video exists and
  `IDriveAutoUpload.IsUploadingNow` (the manifest's `IsBeingUploaded`) says ClassScribe's own upload of the recording goes
  on, since that upload holds the older video open and the new one could not take its place (review 3.5; the job's
  rename failure names both a player and the upload). The continued lesson is the case that matters: the pass after its
  first part waits between pieces while the second part records and, after its stop, while it is transcribed
  (`DriveUploadPass.PauseWhileRecordingAsync`, `HoldingUp` takes `IsTranscribingNow` too), and that wait writes the
  manifest every minute while the queue at the second part's start leaves the stamp, so the stop finds the upload going
  on hours later (`DriveAutoUploadTests.A_paused_upload_still_counts_as_going_on`); the first lesson's start line says how to turn on
  `Drive:LessonVideos` (`LessonVideoDrive.StartLine`). `finish-video <path> [--no-open]` (`FinishVideoCommand`,
  `ICommandRunner.FinishVideoAsync`) makes it again, free, decided from the files alone (`ClassFolders.LessonVideoStateOf`:
  `None`, `Made`, `NotMade` when a piece is newer than the video or there is none), refusing a lesson being recorded or
  made, one whose older video ClassScribe's own upload sends now (above), one without pieces, and one whose sound is on
  Drive only (`SoundOnDriveOnly`), with keep-awake
  (`StayAwake.LessonVideoReason`), then Drive's upload of that recording. The start-up report offers V after an outcome
  whose video was not made (`LessonRuns`, `StartupAction.MakeVideo`, T only when the recording can be transcribed); a
  lesson in Transcribing with only its video left (`LessonRuns.OnlyItsVideoLeft`: record-only, or its transcript saved) is
  worded as its video cut off in the report, the starter's lost recorder, attach and the window-busy lines; menu
  choice 3 notes `lesson video` or `lesson video not made yet` (as `list` does) and takes V before a number. Drive:
  `Drive:LessonVideos` (false) sends `<base>.mp4` last, streamed as `video/mp4` with a line of its size and time, never
  the pieces or the partial (`UploadManifest.OnDisk(files, withLessonVideo)`), and only after `RecordingUpload` has read
  `IDriveRoom.ReadForUploadAsync` (the room for an upload under way: read whatever `Drive:Enabled` says, since 9 > U and
  `drive upload` send with the uploads after each class off too; `ReadAsync`, a plan's, still answers nothing then): a
  video known not to fit in Drive's free room stays with a warning (`does not fit in Google Drive's room`),
  the rest goes up and the recording waits with `NextAttemptUtc` a day later, as Drive full does; a room unknown (not
  signed in, or Drive out of reach) lets it go up as before (teach-video 8, review 3.16); the pass also waits while a class is
  transcribed (N13); `LocalAudioFreer.FreePiecesAsync` deletes the pieces once the video is verified on Drive, only those
  the finish job listed beside it in `RecordingFiles.LessonVideoPieces` (`<base>.mp4.pieces.txt`, written when the pieces
  are kept for Drive, never one of the recording's files, deleted by the freer once every piece it names is gone and by the
  job when it deletes the pieces; a piece left out of the video as unreadable is not in it and stays, named, review 3.15;
  without a list, those not newer than the video; a list that cannot be read keeps them all, with why) and never a newer
  one (it stays, named), and `FreeAsync` refuses a lesson whose video is `NotMade` while its sound is
  here, deleting nothing (`LocalAudioFreer.VideoStillToMake`, with the `finish-video` command). There is no
  `folders.lock` or `MoveJournal` on develop, so `finish-video` and V check neither. Fakes: `FakeLessonVideoMedia`
  (`TestDoubles.LessonVideo.cs`: `Holds`, `Unreadable`, `Unopenable`, `HoldWrite`, `Creep` (the held writing moved on below
  the next percent), `HoldCheck`, `WriteFailure`, `CheckAnswer`, `Written`, `Checks`, the sound's samples). Tests:
  `LessonVideoPlanTests`, `LessonVideoFilesTests`, `CameraFinishJobTests` (the real host over
  the fake media on the manual clock), `LessonVideoMakerTests`, `LessonVideoMediaTests` (WindowsMedia: pieces from the
  real piece writer, a size change encoded again, a continued lesson, IDR-only sync samples, the check's failures),
  `LessonVideoDriveTests`, `RecordCommandTests.LessonVideo.cs`, `FinishVideoCommandTests`, `FinishVideoCommandLineTests`,
  `StartupReportTests.LessonVideo.cs`, and the lesson's EndToEnd run.
- The webcam hold (`Camera/WebcamHold.cs`; camera-first 3.4 as amended on 21 October, behind
  `Recording:WakeCameraMicrophone`, off by default): `TrackSetFactory` gives each microphone track a `WebcamHold` from
  `WebcamHolds` (registered in `CameraServices`; optional, so a factory without it has no webcam). At each pick
  (`TrackSet.OnMicrophonePicked`) `Follow` finds once per microphone whether it belongs to a webcam
  (`Cameras.WebcamOf`: `CameraOf` but never a virtual camera; the cameras are listed only when `Cameras.MayBeOfACamera`,
  a container that is not the computer's own, on the pool: with the setting on waited for at most `ListLimit` (5 s; while
  a listing hangs a later pick begins none and waits for none), and with it off never waited for (release 0.5.0 review,
  3.6, `ListedInTheBackground`: the pick that begins the listing decides nothing, the microphone's next look, 10 s on,
  takes its answer, well within the 60 s the webcam zeros rule at the start needs it by, so a class's start path waits on
  no camera list; a lesson with the picture, whose hold is passed off, lists the same way); a list that has a problem,
  throws or does not come leaves the pick undecided, said once per stretch, a warning only with the setting on, and is
  asked again at the next pick; `Follow` never throws, since it runs inside the microphone's pick). With the setting on it starts a
  camera process with a hold job carrying the camera's id and name alone, and `MicrophoneProviderTap`'s `beforeOpen`
  (`WebcamHold.BeforeOpen`) holds the microphone's capture until the first frame, at most `FirstFrameWait` (5 s) after the
  hold began; the track is `ClockAligned`, so the wait is padded. The hold lasts through P and V off, follows the pick to
  another webcam or lets go for a microphone of none (the old process let go in the background), and `TrackSet.Stop` lets
  it go after the tracks have stopped, before the upload (`stop`, at most `StopWait` 5 s, then killed and disposed); leaving
  the microphone out stops it too. Each line to the process (the job, the stop) goes within `SendLimit` (5 s), else the
  process is killed as stuck and the send fails as an `IOException`, as `CameraTrack`'s lines do (release 0.5.0 review,
  3.7). A process that does not start or take its job (for any cause) or ends by itself is
  started again `RetryEvery` (5 s) later; only the current webcam's start clears its starting flag, so a late start for an
  earlier webcam is let go without letting a look start a second process for the new one, and a start that finds the
  hold's process already there is let go too. The one-second timer (`Look`, which never throws: a timer thread's exception
  would end the process) also says no picture yet (after 5 s), the picture stopped (3 s
  without a frame, `WebcamHold.Stopped`, short `camera's picture stopped`) and back (`is open again`); a refusal from the
  process is said once per cause (`WebcamHold.CannotOpen`, short `camera not opened`). Its lines go to the log under the
  category `Camera`; the process's own log lines go there at Debug, its warnings as warnings. `IMicrophoneRecording.Webcam`
  (no default body, so every recording with the microphone must say) gives the run a `WebcamState` (name, holding,
  frames arriving, problem and its short words); `TrackSet.Webcam` reads it, and `MicrophoneChecks.WatchTheHold` makes
  the problem a Notice with the class, an Alert in person. Tests `CameraHoldTests` (through the real track set too: the
  job before the microphone's open, read through `IMicrophoneRecording`; a camera list that throws or never answers with
  the setting on and off, the microphone still opening; with the setting off the pick returning at once and the webcam
  known by the next look; the stop's line within `SendLimit`; a start that fails for any cause; the late start for an earlier
  webcam), with `FakeCameraProvider.Failure` and `Hangs` and `FakeCameraProcessStarter.HoldStart` and `StartsEntered`.
- The webcam zeros rules (camera-first 3.4; `MicrophoneChecks.WatchWebcamZeros`), in place of today's only-silence warning
  for a microphone whose `IMicrophoneRecording.Webcam` is set, unless Windows flagged its last packet silent or Windows
  mutes it (asked once per stretch of zeros), which keep today's warning; every other microphone keeps it as it was. The
  zeros are counted at the device's own depth before the 16-bit conversion (`PcmConverter.AnyNonZero`; a negative zero is
  zero; packets flagged silent count as neither), carried as `RecordingStatus`/`TrackSnapshot` fields with defaults
  (`RawZerosFor`, `RawSoundSinceOpened`, `WindowsSilent`, `CaptureNumber`). At the start: `WebcamZerosAtStart` (60 s) of
  zeros since the open, once a capture, in words that name the light, or, with the hold's frames arriving, the
  microphone's own switch (`MicrophoneChecks.WebcamStartText`). Later: `WebcamZerosAfterSound` (2 min) after sound, once a
  recording, never while the hold's frames arrive, its Notice ending when sound returns (`WebcamLaterText`). Each a Notice
  (`only zeros from the webcam's microphone`) with the class, an Alert in person. Both lengths sit beside
  `RecordingSession.OnlyZerosAfter`. The words name OBSBOT Center (Device Auto Sleep, the microphone's switch) only for a
  camera whose name holds `OBSBOT` (`Cameras.IsObsbot`, `SleepSettingOf`, `MicrophoneSwitchOf`); another make is sent to
  its maker's app, and is not said to send exact zeros in silence, which is known of the OBSBOT alone; `WebcamHold.CannotOpen`
  names the sleep setting the same way (release 0.5.0 review, 3.9). Tests in `WebcamZerosTests.cs` (partial `RecordingSessionTests` for the measure, each
  capture counted afresh, and partial `RecordingMonitorTests` for the rules: quiet while paused, switched off or not
  listened to, the hold's problem an Alert in person, finding 4's twelve awake minutes replayed with no line).
- The camera check (`CameraCheckCommand`, menu choice 8 then C and `camera-check ["<camera>"] [--resolution <size>]`,
  through `ICommandRunner.CameraCheckAsync` in a scope of its own; teach-video 6.16, camera-first step 11 point 3): the
  colour cameras numbered, with each webcam's own microphone; which apps use a camera now, from Windows' camera record
  (`CameraUsageRecord`, ConsentStore\webcam, read by `ConsentStoreRecord` as the microphone's record is); the camera
  and size a lesson with `--camera` would take; and the set-up lines of `CameraSetupFacts` (`Camera/CameraSetup.cs`, which
  setup-check will reuse): Windows against 10.0.19041, the three privacy switches (the device's under HKEY_LOCAL_MACHINE,
  the user's and the desktop apps'), an H.264 encoder (WinRT's `CodecQuery`), the antivirus programs that are on
  (Security Center's `WSCProductList` through `IWscProductListRaw` and `IWscProductRaw` in
  `Camera/Windows/SecurityCenterInterop.cs`: IDispatch's four slots first, since Windows registers no type library for
  late binding; Bitdefender gets its Webcam Protection step), each webcam's microphone, and the hold. Then, only after a
  typed Y and Enter (`CameraCheckCommand.Question`; anything else is `NotOpened`, a window that takes no typing
  `NotAsked`), it opens the chosen camera for `OpenFor` (5 s), shared, at the picture's size, through a camera process's
  check job, and shows every call with its outcome and timing, the camera's format, the frames, or why none came and
  `TryAgain`; 75 s in all (`CheckLimit`) before the process is ended. Exit 0, or 1 when a `PROBLEM` line keeps the camera
  from being used or the camera opened sent no picture or could not be opened. Never in a test with a real camera: the
  tests' starter runs the real host over `FakeCameraCapture`. Menu choice 8 asks W (the microphone check) or C. Fakes:
  `FakeCameraSystem`, `FakeCameraUsageRecord`; tests `CameraCheckTests` (with `CheckLimit` reached on the manual clock), and the real reads in
  `WindowsCameraProviderTests` (WindowsCamera).
- The microphone check (`MicCheckCommand`, menu choice 8 then W and `mic-check`, resolved through
  `IMicCheckRun` in a scope of its own): MicWatch inside ClassScribe. Every 250 ms it reads
  `IMicrophoneSessions.ReadAll()` and `IMicrophoneUsageRecord.Read()`, hands them to the pure
  `MicrophoneChangeTracker` (MIC, SESSION, PRIVACY, MARK, NOTE, ERROR and SELF lines, MicWatch's
  formats; another ClassScribe process is `ClassScribe, a recording in progress (pid N)`), writes
  each line to `MicCheckLog` and the window, Q or one Ctrl+C stops it, M marks, `--seconds` stops it
  by itself, a gap of 2 s or more gives a NOTE, and the title is `MIC CHECK 00:12:40, 23 lines  Q
  stops`. It runs in the window, takes no lock, and may run beside a recording.

## Layout

The source is four projects under `src` (architecture 1.1 and 1.3; the move is slice 4, `feature/layers-move`: every moved
file an exact rename along `design\final\architecture-analysis\move\move-map.tsv`, then each project's namespaces given
its name): `ClassScribe.Core` (`ClassScribe.Core.dll`, plain `net10.0`, no package), `ClassScribe.Application`
(`ClassScribe.Application.dll`, plain `net10.0`; `Microsoft.Extensions.Logging.Abstractions`, `Options` and
`DependencyInjection.Abstractions`), `ClassScribe.Infrastructure` (`ClassScribe.Infrastructure.dll`,
`net10.0-windows10.0.19041.0`; `NAudio.Core` and `NAudio.Wasapi`, `Refit.HttpClientFactory`, the configuration, HTTP,
console logging and options binding packages, and `System.Security.Cryptography.ProtectedData`) and `ClassScribe.Cli`
(`ClassScribe.exe` and `ClassScribe.dll`: its `AssemblyName` is `ClassScribe`; `net10.0-windows10.0.19041.0`;
`Microsoft.Extensions.Hosting` and `System.CommandLine`). Each area keeps its folder name inside each project, and its
namespace is the project's name followed by the folder (`src/ClassScribe.Core/Audio/Timeline` is
`ClassScribe.Core.Audio.Timeline`), so a path given by its area alone elsewhere in this file
(`Audio/Windows/CoreAudioInterop.cs`, `Desktop/DeviceGuards.cs`, `Drive/DriveServices.cs`) is under the project this
section places that file in. The four share `src/Directory.Build.props` (the `<Version>` line, nullable, implicit usings,
`SatelliteResourceLanguages`, `GenerateDocumentationFile` with `PublishDocumentationFile` off, `EnforceCodeStyleInBuild`,
and the one `InternalsVisibleTo` for `ClassScribe.Tests`; it lives in `src`, not at the repository's root, so that
`tools/dry-run/TestPlayer` and the file-based `ElevenLabsStub.cs`, which would read a root props file too, stay as they
are), the root `.editorconfig` (IDE0005 as a warning) and `ClassScribe.sln` (the four projects under its `src` solution
folder, the tests under `tests`). Core and Application take back the implicit `System.Net.Http` using, and Core `System.IO`
too, so a file there that reaches for a file or the network shows its own `using` line. The Cli project sets
`PublishReferencesDocumentationFiles` false, so no XML file is published, and copies `appsettings.json` to its output
(`PreserveNewest`). The old `src/ClassScribe` folder is gone: a worktree that checks out the move from an older commit
keeps its ignored `bin` and `obj` there, which `RepositoryLayoutTests` skips.

Since architecture slice 3a (`feature/layers-cut`), every file holds one layer's types (Core, Application, Infrastructure or
Cli, as `architecture.md` section 3 places them), which the move then put into the project of that layer: where this
section names a file with the types it declares, a type of another layer has a file of its own, in the same area folder of
its own project, named after its main type. `design\final\layers-cut-map.md` (beside the design, outside the repository)
lists every such move, the members moved inward (`Sizes`, `FolderNames`, `WavHeader`, `Room`, `RunTimings`, `LinkVersion`,
`ProgramFiles`, `RunView.IsRunOf`) and the visibility changed.
Slice 3b1 (`feature/layers-storage`) put the recording's files and storage behind ports, interfaces in Application whose
adapters are the classes that held the code (architecture 4): `IClassFolders` over `ClassFolders` (its disk statics stay
static for Infrastructure's own callers and are the port's members too, implemented explicitly over them), `IRecordingFolder`
(`RecordingFolder`: the disk queries that were `RecordingFiles`' members), `ITranscriptCopies`, `IClassMarks`, `IMicCheckLog`
with `IMicCheckLogFile` (`MicCheckLog`, `MicCheckLogFile`), `ITrackWriterFactory` with `ITrackWriter` (`WavTrackWriterFactory`
over `WavSegmentWriter`: the one way a recording session reaches the disk), `IAudioFileFacts` (`AudioFileFacts`) and
`ITranscriptAnswers` (`ElevenLabsAnswer`), with `IFileProbe`'s new checks and `IAudioCapture.Processing`. A use case takes a
port through its constructor; a static helper of a use case (`LessonRuns`, `DownloadPlan.Lines`, `VideoPlan.Lines`,
`RecordingUpload.BusyIn`) takes it as a parameter. `design\final\layers-storage-map.md` lists each member's old and new call
and each constructor that takes a port.
Slice 3b2 (`feature/layers-runs`) put the runs behind ports the same way, in `Recorder` (architecture 3.9: beside
`IProcessLiveness` and `IVideoYield`, Application's types there already): `IRuntimeFolder` over `RuntimeFolder`,
`IRecordingLock` over `RecordingLock`, `IRunJournal` with `IRunHandle` over `RunJournal` and `RunHandle` (kept together: the
handle uses the journal's internal `Write` and `Logger`; `RunJournal.Begin` still gives a `RunHandle`, for the recorder's host
and the tests, and the port's `Begin` is explicit over it), and `IRunLog` over `RecorderLog` (the lines as they are written,
the copy into the recording's `.log`, and `LastStartupLines`, the read of recorder.log that `RecorderLauncher` made itself;
`Written`, `Since` and `WaitForFollowers` are the link's and stay on the adapter). Two constants moved with their callers:
`RunJournal.KeepSeenRunsFor` is `RunTimings.KeepSeenRunsFor` (Core) and `RecorderLog.StartupFileName` is
`IRunLog.StartupFileName`. The window's commands (`AttachCommand`, `ContinueQuestion`, `StartupReport`, `CommandRunner`) take
the ports too; the recorder's host and Infrastructure's own users keep the adapters. `design\final\layers-runs-map.md` lists
each member's old and new call and each constructor that takes a port.
Slice 3b4 (`feature/layers-program`) put the program and the prompts behind ports: `IThisProgram` (`Desktop`: the program's
path, the folder of its files, this process's id and its start, over the record `ThisProgram`, which `ProcessStarter` and
`CameraProcessStarter` keep using for CreateProcessW's command line), which `CommandLine` (a class now, one for the process;
its explicit `(processPath, appFolder)` forms stay static), `RunBookkeeping`, `TranscriptionRuns`, `MicCheckCommand` and
`DevicesCommand` (`AppsByName` and `Describe(microphone, listing)` take it as a parameter, which `MenuCommand` and
`ClassesMenu` pass) read in place of `Environment.ProcessPath`, `AppContext.BaseDirectory`, `Environment.ProcessId` and
`Process.GetCurrentProcess`; every class that prints a command takes the `CommandLine` (`LessonRuns`,
`LessonVideoDrive.StaysHereLine` and `LocalAudioFreer.VideoStillToMake` as a parameter; `CameraFinishJob`, in the camera
process, over `ThisProgram.Current`); `IContinuePrompt` (`Runs`, with `ContinueAnswer` beside it) over `ContinueQuestion` and
`ICameraPrompt` (`Runs`, with `NothingStarted`, moved from `CameraStart`) over `CameraStart`, which `RecordStarter` takes; and
`IRunStarter` (`Runs`: the one call `RecordAsync(request, origin)`), which `ICommandRunner` extends, `CommandRunner` answers
in a scope of the run's own, and `VideoBatch` takes. `IServiceScopeFactory` stays in `RecordStarter` and
`ScopedVideoTranscriber` (architecture question 5). `design\final\layers-program-map.md` lists each member's old and new
call and each constructor that takes a port.
Slice 3c (`feature/layers-vendors`) put Drive, YouTube and the camera behind ports the same way, and ran the trial move
(architecture 11.6; `design\final\trial-move.md`, with the move and namespace scripts and the architecture tests in
`design\final\architecture-analysis\trial`, which slice 4 committed from its copy in `...\move`). Drive (`Drive`):
`IGoogleDrive` (with `DriveItem` and `DriveAccount`, Google's answers mapped: the account, an item, a folder's children,
the root and the path, `SendAsync` for one file) over the thin adapter `GoogleDrive`, which forwards to `DriveClient`,
`DriveFolders` and `ResumableUpload`; `IGoogleAccount` (with `GoogleAnswer`: the sign-in's exchange and revoke, `Seed`,
`Forget`, `NotSetUp`, `NotSignedIn`) over `GoogleTokens`, whose `SendFormAsync`, `Parse` and `Describe` stay internal;
`IGoogleClientFiles` (`Read(path)`) over `GoogleClientFile`, an instance now; `IUploadManifests` (its `OnDisk` explicit over
the kept static) and `IDriveUploadLock`; the disk calls of `RecordingUpload`, `LocalAudioFreer` and `DriveCopies` through
`IRecordingFolder` (`FilesOf`, `OpenToRead`, `Delete`, `IsUploadCopyInUse`) and `IFileProbe` (with `Describe`, one look at a
file's length and last write, for `RecordingUpload.IsKnownInSync`, so that a file deleted between two looks is not in sync
rather than an error); `ILoopbackRequest.AnswerAsync` passes a browser that has gone over or throws an `IOException` (the
listener's adapter wraps its `HttpListenerException`) or an `ObjectDisposedException`, never the listener's own type, which
`LoopbackReceiverTests` and `GoogleSignInTests` (`BrowserPlay.ClosesTheTab`) pin. YouTube (`YouTube`): `IVideoTool` (with
`VideoInfo`, `ListedPlaylist`, `ListedVideo`: `CheckAsync`, the `ToolCall`s `ReadPlaylist`, `ReadVideo` and `Download`,
`Playlist`, `HasInfo`, `ReadInfoAsync`, `StagedWav`, `Classify`) over `YtDlpTool`, scoped, which holds `YtDlpCheck`'s paths
and builds and checks every argument list through `YtDlpArguments` as before, while the batch still runs the tool through
`IToolRunner`; `IVideoTitles` over `OEmbedTitles`; `IVideoIndex` (the index itself, scoped: `Read(root, logger)`, `Judge`,
`Of`) over `VideoIndex`; `IVideoSources` over `VideoSourceFile`'s statics; `IDownloadedAudio`; `IVideoWindowOpener` over
`VideoWindow`; `IToolLocator` (`FindBrowser`, `BrowserVersionOf`, `BrowserNotFound`) over `BrowserLocator`; `IYouTubeLock`;
`IYouTubeFolders` (with `MakeRoot`, `MakeStaging`, `DeleteStaging`); and `YouTubeOptions.Expanded` moved to Infrastructure
as `SettingPaths.Expanded` (`Settings/SettingPaths.cs`). The camera (`Camera`): `ICameraLink` (with `CameraLine`: the sends
and `ReceiveAsync`) over `CameraLink`, an adapter over `CameraLines` and a `LineChannel`, so `ICameraProcess.Channel` is
`ICameraProcess.Link`; `IWebcamHolds` with `IWebcamHold` over `WebcamHolds` and `WebcamHold`, which `MicrophoneTrack`,
`TrackSet` and `TrackSetFactory` take. Each port is registered right after its adapter with the adapter's lifetime, and
`Lazy<DriveClient>` became `Lazy<IGoogleDrive>`. `design\final\layers-vendors-map.md` lists each member's old and new call,
each constructor that takes a port, and what the trial move made the one-project tree change (its section 6).

- `src/ClassScribe.Core` (namespace `ClassScribe.Core`; architecture 1.1: ClassScribe's own domain, with nothing of
  Windows, the vendors, the disk or the services; what it may not use is checks 2 and 3 of the architecture test, and what
  it keeps of System.Text.Json is check 4). At its root `Durations` (with `Hours`) and `Sizes` (the sizes in Google One's
  units, `Sizes.Describe`), the words for durations and sizes ClassScribe prints everywhere.
  - `Core/Audio`: the audio packets, tracks, modes and snapshots: `AudioDataEventArgs.cs` (`AudioDataEventArgs` with
    `Qpc100ns`, `CaptureStoppedEventArgs`, `PacketFlags`), `AudioFormat`, `PcmConverter`, `MicrophoneOpened`,
    `OutputDevice.cs` (`OutputDevice`, `CaptureTarget`, `PlayingApp`, `PlayingAppsListing` with its `UnreadAudioSession`s),
    `RecordingMode`, `Track`, `TrackSpec.cs` (`TrackSpec`, `Contiguous` or `ClockAligned`, and `TrackPlacement`),
    `RecordingResult.cs` (`RecordingResult`, `RecordingStatus` with `SinceLastPacket`, `DeliversContinuously`,
    `RecordedLevel` and `HeardSound`), `RecordingSnapshot.cs` (`RecordingSnapshot`, `RecordingSummary`, `TimelineNote`,
    `TrackSnapshot` with `HeardSound`), `SourceWording`, `UploadTracks.cs` (`UploadTracks`, the tracks the upload copy is
    mixed from, and `NoAudioException`) and `WavHeader` (the header ClassScribe writes: its size and its parse from bytes;
    `AudioFileFacts` reads a file's length through it).
  - `Core/Audio/Timeline`: the QPC timeline of design section 3: `MediaTime` (positions in 100-ns units, conversions
    without overflow), `RecordingClock` (origin, offset, pauses and cuts, positions, the paused and cut totals,
    `Snapshot()`), `ClockState.cs` (`ClockState`, the clock's whole state, immutable, placing by the clock's own code, which
    the camera process places a lesson's frames by; `ClockExclusion`) and `TrackPlacer.cs` (`TrackPlacer`: where a packet
    goes on a track, the padding target, the drift corrections; `Placement`, `PlacementKind`). Moved as they were: nothing
    in them looks outside.
  - `Core/Audio/Microphone` (namespace `ClassScribe.Core.Audio.Microphones`, plural on purpose, in each of the three
    projects that have the folder: a `Microphone` namespace would hide the `Microphone` record): the microphone's records,
    looks and the gate's verdicts: `Microphone.cs` (`Microphone`, with `ContainerId`, a last field with a default, read by
    `EndpointFacts` and carried for a webcam's microphone; `MicrophoneProcessing`; `ClassAppMute`; `FollowRule` with
    `TryParse`, `Word` and `Named.Matches`, and its `[JsonPolymorphic]` `kind` names; `MicrophoneGateState`),
    `CaptureSession.cs` (the readers' listings: `CaptureSession`, `CaptureSessionState`, `MicrophoneListings`,
    `MicrophoneSessionListing`, `UnreadCaptureSession`, `AppListening`, `MicrophoneRoles`), `UsageRecordListing.cs`
    (`UsageRecordListing`, `UsageEntry`), `WatchLook`, `GateVerdict.cs` (`GateVerdict`, `GateSummary`, `IPacketGate`),
    `MicCheckLine` (the check's lines), `MicrophoneAdviceLine.cs` (`MicrophoneAdviceLine`, `MicrophoneStart`),
    `MicrophonePick` and `BluetoothMicrophoneLook.cs` (`BluetoothMicrophoneLook`, `BluetoothMicrophoneUse`).
  - `Core/Camera` (teach-video section 6 and camera-first step 11): the camera's frames, pieces and the camera process's
    messages: `CameraDevice.cs` (`CameraDevice`, `CameraPlace`, `CameraPanel`, `VideoDeviceFacts`, `CameraList`, and
    `Cameras`: the list's rules, colour cameras only, so Windows Hello's infrared camera never shows, built in by the
    enclosure panel first since a laptop's own camera sits on an internal USB bus too, then USB or virtual by the id;
    `Choose`, a name part first, virtual ones included, else USB, else the built-in front camera, never a virtual one
    unnamed; `CameraOf` and `MicrophoneOf`, a webcam's microphone by the container it shares with its camera, never a
    built-in camera and never by the computer's own container `Cameras.ThisComputer`; `WebcamOf`, never a virtual camera
    either, for the hold and the zeros rules), `CameraPieces.cs` (`PieceSettings`, `ICameraFrame`, moved here by hand since
    it only describes a frame, its stamp, its size and a copy of its pixels, which Windows' frame implements in
    Infrastructure; `CameraPicture`, `FramePlacer`, `FrameQueue`, `FrameFate`, `FramePlacement`, `FrameRefusal`,
    `QueuedFrame`), `CameraProtocol.cs` (the camera process's lines: `CameraProtocol` with `IsCameraProcessStart`, which
    `Program` asks; `CameraJob`, `CameraJobKind`, `CameraOpened`, `CameraProgress`, `CameraProblem`, `CameraProblemKind`,
    `CameraPiece`, `CameraStop`, `CameraStatus`, `CameraLog`, `CameraMade`, `CameraFailed`, `CameraEmpty`),
    `CameraSetupLine.cs` (`CameraSetupLine`, `CameraSetupState`), `CameraSize`, `CameraTrackStart.cs` (`CameraTrackStart`,
    `CameraTrackState`, `CameraConcern`, `CameraOffsets`, `CameraNotBuiltException`), `LessonVideo.cs` (`VideoSourceFacts`,
    `LessonVideoPlan`, `LessonVideoCheck`, `LessonVideoSource`, `PlannedSlice`) and `LessonVideoResult.cs`
    (`LessonVideoResult`, `LessonVideoOutcome`).
  - `Core/Desktop`: `ProcessArguments` (the `--recorder` and `--camera-process` rule, the same as `RunIds`'; `IsRunId`)
    and `ProgramFiles` (`Name`, `Exe`, `Dll`: the program, its exe and its dll, the same however it was started; check 7
    of the architecture test ties `Dll` to the Cli assembly's name, since a recorder or camera process under dotnet starts
    it; `IOwnProcesses` finds ClassScribe's processes by `ProgramFiles.Name`).
  - `Core/Recorder`: `RunRecord.cs` (run file, request file: `RunRecord`, `RunRequestFile`, `RunState`, `RunHome`,
    `RunOutcome`, `OutcomeKind`, `RunEndCause`, the video causes among them), `RunView` (a run as the window, the report at
    the start and the kit read it: `IsRunOf`, `IsVideo`, `NeedsReport`) and `Freeze` (a time when ClassScribe did not run:
    the laptop slept, or Windows held it up).
  - `Core/Runs` (namespace `ClassScribe.Core.Runs`, out of `Commands` in slice 3a): the run's contracts, which every area
    describes a run with: `Requests.cs` (`RecordRequest`, `SourcePlan`, `ClassSource`, `MicrophoneChoice`, `CameraChoice`,
    `VideoRequest`, `ContinueChoice`, `StartOrigin`, `RunIds`; `RecordRequest`, `SourcePlan` and `RunRecord` keep
    `[JsonExtensionData]`, `ClassSource` its `kind` names), `RecorderState.cs` (`RecorderState`, `RecorderPhase`,
    `Attention`, `AttentionLevel`, `MicrophoneState`, `CameraState`), `ViewResult.cs` (`ViewMode`, `ViewEnd`,
    `ViewResult`), `StartupChoice.cs` (`StartupAction`, `StartupChoice`), `RunTimings` (the heartbeat's interval, and
    `KeepSeenRunsFor`, how long a seen run's file is kept) and `LinkVersion` (the link's version, which
    `LinkProtocol.Version` names and every run file records).
  - `Core/Settings`: `SavedClass.cs` (`SavedClass`, `SavedClassEntry`, `ClassFields`; the check of a class is
    `SavedClass.Check`) and `SettingsProblem` (keyed problems, for the recording's drop of refused settings).
  - `Core/Storage`: `RecordingFiles` (the naming alone: both tracks, `_partN`, `.mic.wav`, `.marks.txt`, `.log`, `.md`,
    `.json`, `.upload.wav`, `.unread.json`, `.source.json`, `.glossary.json`, `.mp4`, a lesson's camera pieces, and
    `TryParse`), `CameraPieceFile`, `ClassMark` (a mark or note of `.marks.txt`; `ClassMark.PartsIn` counts the parts),
    `AudioOnDrive` (what is known of a recording's audio kept on Drive alone), `FolderNames` (the recordings' folders as
    pure rules: a name made safe for a Windows folder, the date folders of a course, `2026-10-17` and `2026-10-17 (2)`, and
    the folder of the microphone check's logs; nothing here reads the disk) and `RecordedClass.cs` (`RecordedClass`, a
    recording as the lists see it, with `OnDrive` and `Video`; `ContinueCandidate`; `LessonVideoState`).
  - `Core/Transcription`: the transcript records and the glossary's rules: `TranscriptionResult.cs` (`TranscriptionResult`,
    `TranscriptWord`), `TranscriptFiles.cs` (`TranscriptFiles`, `RecordingTooLongException`), `ChargedAnswer`,
    `CostEstimate`, `UsageCharge`, `MarkdownText` (a video's title escaped for the transcript), `KeytermRules.cs`
    (`KeytermRules`, `KeytermLimits`, `KeytermList`, `SkippedKeyterm`: one term per line, `#` for comments, the limits and
    what is skipped), `GlossaryRecord.cs` (`GlossaryRecord`, what `<base>.glossary.json` holds, `GlossaryDecision`,
    `GlossarySent`) and `GlossaryPick.cs` (`GlossaryPick`, the glossary a run or a transcript takes before it is read, which
    `RecordRequest.Glossary` carries to the recorder; `GlossaryUse`).
  - `Core/YouTube`: `VideoSource.cs` (`VideoPlaylist`, `VideoSource`: what a video's recording keeps beside it in
    `<base>.source.json`, which the transcript formatter and the recordings' library read like any recording's files).
- `src/ClassScribe.Application` (namespace `ClassScribe.Application`): the use cases, the services they drive, the options
  and the catalog, the ports and the records the ports exchange. At its root `ApplicationServices`
  (`AddClassScribeApplication`: each area's `<Area>UseCases`, see Conventions) and `BuildInfo` (the version and commit from
  its own assembly's informational version, the same in all four: the menu's header, `--version`, the run files, the logs,
  the microphone check's log and the link's hello and welcome name the build through it, so that a window and a recorder
  of different builds can be told apart).
  - `Application/Audio`: `RecordingSession` (one track's capture: it places `ClockAligned` tracks through Core's
    `Audio/Timeline`, holds packets for a gate, pads to the clock on its own one-second timer, reports `Off`, `OnlyZeros` and
    `Corrections`, and reaches the disk only through `ITrackWriterFactory`), `IRecording.cs` (`IRecording`, the recording a
    run drives, and `IRecordingFactory`), `ICaptureSource` and `LoopbackSource` (what the computer plays, through
    `CaptureChooser`, which follows the run's `ClassSource`, and `ChooseVideo` for a video window), `ClassRecording.cs`
    (`ClassRecording`, an `IRecording` over one session of the class alone, and `ClassRecordingFactory`), `TrackSet.cs`
    (`TrackSet`: the class and the microphone on one clock, or the microphone in person; owns the gate and the watch;
    `MicrophoneTrack`, and `MicrophoneProviderTap`, the provider as a recording sees it, noting each capture opened with the
    processing it reports through `IAudioCapture.Processing`), `TrackSetFactory.cs` (the registered `IRecordingFactory`: the
    recording per mode, and which app is followed), `SourceChoice` (the picks of menu choices 6 and 7, or the settings, as a
    `SourcePlan`), `AudioFailures` (`IsFailedResult`, `CallKey`, `Describe`), the ports `IAudioCapture.cs` (`IAudioCapture`
    with `Processing`, `ILoopbackCaptureProvider`), `IAudioPreparer`, `ITrackWriterFactory.cs` (`ITrackWriterFactory` and
    `ITrackWriter`: the write path a recording session reaches the disk by, its folder, next part, delete and length),
    `IAudioFileFacts` (a WAV file's length from its header), `IMicrophoneRecording.cs` (`IMicrophoneRecording`, what a
    recording with the microphone tells the run beyond its snapshot, and `ICameraRecording`, which `TrackSet` implements)
    and `AudioUseCases` (`AddAudioUseCases`).
  - `Application/Audio/Microphone` (namespace `ClassScribe.Application.Audio.Microphones`): `IMicrophoneProvider.cs` (the
    contracts `IMicrophoneProvider` and `IMicrophoneCaptureFactory`), `MicrophoneChooser.cs` (which microphone, pinned by
    id), `MicrophoneAdvice.cs` (the start lines, the echo check, the Vantage poll), `MicrophoneSource.cs` (the microphone
    as a capture source: chooser plus provider, `Picked`, `FollowApp`), `MicrophoneWatch.cs` (`ClassMicrophoneWatch`, and
    the readers' contracts `IMicrophoneSessions` and `IMicrophoneUsageRecord`), `MicrophoneGate.cs` (`MicrophoneGate`,
    pure but for its logger, which is why it is here and not in Core), `MicrophoneChangeTracker.cs` (the check's lines)
    and `BluetoothMicrophoneWatch.cs` (the class app on a Bluetooth microphone, registered scoped in `AudioUseCases`
    beside the advice).
  - `Application/Camera`: `ICameraProvider.cs` (`ICameraProvider`, `CameraListing`), `CameraRoute.cs` (the shared open and
    its call log: `CameraRoute`, `CameraCall`, `CameraCallLog`, `CameraCallFailedException`, `CameraFormats`,
    `CameraHresults`, an HRESULT in words with its name where known for certain, `CameraOpening`, and the ports `ICameraCapture` and
    `ICameraCaptureFactory`), `CameraProblems` (what a camera process says when a camera cannot be opened: the kind, and
    the cause in words, for the route and for the camera process's jobs), `CameraProcess.cs` (`ICameraProcess` with `Link`, `ICameraProcessStarter`), `CameraSetup.cs`
    (`CameraSetupFacts`, `ICameraSystem`, `ICameraUsageRecord`: Windows' record of which apps used a camera, with its
    privacy switches), `CameraRecorder.cs` (`ICameraRecorder`, `CameraRecorder`, `ICameraTrack`, `CameraRecorderNotBuilt`),
    `CameraTrack.cs` (the recorder's side of a lesson's picture; `NextFreePieceStart` asks `IFileProbe`), `ICameraLink.cs`
    (`ICameraLink`, `CameraLine`: the camera process's lines as the use cases speak them), `IWebcamHolds.cs`
    (`IWebcamHolds`, `IWebcamHold`), `IPieceWriter.cs` (`IPieceWriter`, `IPieceWriterFactory`), `LessonVideoMaker.cs` (the
    starter's side of the finish job: one path for the recorder after a lesson, `finish-video`, menu choice 3's V and the
    report's V, with `ProgressLimit` against a stuck video), `WebcamState` and `CameraUseCases` (`AddCameraUseCases`).
  - `Application/Commands`: the use cases of the commands, and the ports of the person and the desktop: the `record` run
    (`RecordCommand` as the `IRecordRun` in either home, `RecordingLoop` for the keys, states and heartbeat,
    `RecordingMonitor.cs` for the once-a-second checks, which also declares `MonitorContext`, `MonitorTick`,
    `MicrophoneWords` and `MicrophoneChecks`, the microphone's checks beside the class's, `VideoEndWatch.cs`, internal: how
    a video's recording ends by itself, for `RecordingMonitor`, `RunBookkeeping.cs` for the lock, the run file, the
    recording's log, keep-awake and the end handlers, with `RunContext`, `RunContextSlot` and `ICrashWarnings`,
    `WindowResultShower`), the starter (`RecordStarter`, `RecorderLauncher.cs` with `LaunchResult` and `RunHomeChoice`),
    `transcribe` (`TranscribeCommand`, `TranscribeRequest`), `rebuild`, `finish-video` (`FinishVideoCommand`,
    `FinishVideoRequest`), `list`, `devices` (output devices, apps with sound, then the microphones with which apps hold
    them, from `Lazy<IMicrophoneProvider>` and `Lazy<IMicrophoneSessions>`, then the cameras), `MicCheckCommand.cs` (the
    microphone check's loop) with `IMicCheckRun.cs` (`MicCheckRequest` and the `IMicCheckRun` seam), `CameraCheckCommand`
    (`CameraCheckRequest`) and `CameraDecision`, Google Drive's commands (`DriveCommand`: `drive signin`, `signout` and
    `status`; `DriveUploadCommand`: `drive upload` and `drive free-space`, and what a path names), `YouTubeCommand`,
    `settings` (`SettingsCommand`, `SettingsRequest`, `SettingsAnswer`, `SettingRow`, `SettingSource`) and `class`
    (`ClassCommand`, `ClassRequest`, `ClassAnswer`, `ClassShown`), the use cases, which answer with data that the window's
    `SettingsOutput` and `ClassOutput` write on `CommandOutput` as text or JSON (architecture question 4), `CommandLine`
    (a class, one for the process, over `IThisProgram`, for the commands printed for later), `LessonRuns` (the report's
    lesson video), `Sentence.cs` (`Sentence`, `LocalTime`), the ports `IConsoleUi`, `IUserSignals`, `IDesktop.cs`
    (`IDesktop`, `DesktopExtensions`) and `IExitWarnings`, and `CommandUseCases.cs` (`AddCommandUseCases`, which also adds
    `FreezeWatch` with a run's use cases, `CommandLine` as a singleton and the stand-in `NoStartupReport`).
  - `Application/Desktop`: the desktop's ports and the records they exchange: keeping the laptop awake
    (`IPowerRequests.cs`: `IPowerRequests`, `IPowerSource`, `StayAwake`, `PowerNeeds`, `NoPowerRequests`,
    `UnknownPowerSource`), the computer's clocks (`ISystemTimes`), the window's title, taskbar and flash
    (`IWindowSignals.cs`: `IWindowSignals`, `NoWindowSignals`, `TaskbarState`; `TitleText`, the title and taskbar button
    for a run's state), starting processes (`IProcessStarter.cs`: `IProcessStarter` with `StartBrowser`, `StartedProcess`,
    `StartRefusal`), the job check (`IJobInspector.cs`: `IJobInspector`, `JobFacts`), Windows' end of the session
    (`ISessionEvents.cs`: `ISessionEvents`, `SessionEndEventArgs`, `SessionEndKind`), the registry read only
    (`IRegistryValues`, HKEY_CURRENT_USER for the microphone advice, and `ReadLocalMachineString` for the machine's PATH and
    Chrome's App Paths), the program behind a process id (`IProcessNames.cs`: `IProcessNames`, a limited query,
    `ProcessImage`), ClassScribe's own processes (`IOwnProcesses`, by pid and by `ProgramFiles.Name`), this program as the
    process that runs it (`IThisProgram`: its path, the folder of its files, this process's id and start), `IFileProbe` (a
    file or a folder there, when a file was last written, how long it is, its full path, a program's version, `Describe`:
    the checks the use cases once made with `File`, `Directory` and `Path.GetFullPath`), `IVideoWindows` (a process's
    Chrome windows: titles and `WM_CLOSE`) and `IProcessFamily` (a process's tree by pid and start time).
  - `Application/Drive` (design `drive.md`, slices 1 to 3): `DriveOptions` (with `DriveUploadScope`),
    `GoogleDriveException.cs` (`GoogleDriveException`, `DriveProblem`), `GoogleClient` (the Google OAuth client, with
    `ShortId`), `GoogleCredentialStore.cs` (`GoogleCredentials`, `StoredCredentials`, `IGoogleCredentialStore`), `Pkce`,
    the ports `IGoogleClientFiles`, `ILoopbackReceiver.cs` (`ILoopbackReceiver`, `ILoopbackListener`, `ILoopbackRequest`),
    `IBrowser`, `IGoogleAccount.cs` (`IGoogleAccount`, `GoogleAnswer`), `IGoogleDrive.cs` (`IGoogleDrive`, `DriveItem`,
    `DriveAccount`), `IUploadManifests` and `IDriveUploadLock`, `DriveFolderPath.cs` (`DriveFolder`, `DriveFolderPath`),
    `GoogleSignIn` (`SignedIn`, `SignOutOutcome`; window only, never a recorder), the uploads (`DriveRetry`,
    `UploadSource.cs` with `UploadSource`, `UploadTarget` and `IUploadWatch`, `UploadManifest.cs` with `UploadState`,
    `UploadSession`, `ManifestFile` and `UploadManifest`, `RecordingUpload` with `RecordingOutcome`,
    `RecordingUploadResult` and `UploadNotTrackedException`, `DriveUploadPass` with `PassResult`), `LocalAudioFreer`
    (`FreedAudio`), `LessonVideoDrive` (whether a lesson's video goes to Drive, the start line), `DriveCopies` (the
    `IAudioOnDrive` of `Storage`), `DriveAutoUpload.cs` (`IDriveAutoUpload`, `DriveAutoUpload`: the queue at a run's start
    and the upload after it, and after a transcript made later), `DriveQueueReport.cs` (`DriveWaiting`,
    `DriveQueueReport`: the menu's header line and the report's), `DriveRoom.cs` (`IDriveRoom`, `DriveRoom`, `DriveSpace`:
    Drive's room from the about call, `ReadAsync` for a YouTube plan, null with `Drive:Enabled` off, and
    `ReadForUploadAsync` before a lesson's video goes up, whatever `Drive:Enabled` says; both null when not set up, signed
    out or Drive out of reach) and `DriveUseCases` (`AddDriveUseCases`).
  - `Application/Recorder`: the ports of runs (architecture 3.9 and 4.3): `IRuntimeFolder`, `IRecordingLock`,
    `IRunJournal` with `IRunHandle`, `IRunLog` (`IRunLog.StartupFileName` is `recorder.log`), `IProcessLiveness`,
    `IVideoYield`, `LogEntry` (one numbered line of the recorder's log, which carries a `LogLevel`, which is why it is
    here and not in Core), `RecorderStart` (what a window hands the recorder: the run id, the request and the settings
    snapshot, never the key) and `FreezeWatch` (the loop's ticks plus a 1-second timer of its own: sleeps and hold-ups of
    3 s or more, told apart by Windows' interrupt times).
  - `Application/Runs`: `IRecorderStatusSink.cs`, `StarterContracts.cs` (`IRunViewer`, `IRecordStarter`, `IRecordRun`,
    `IStartupReport`, `NoStartupReport`, `IResultShower`), and the ports of slice 3b4 that the window answers
    (`IContinuePrompt.cs` with `ContinueAnswer`, `ICameraPrompt.cs` with `NothingStarted`, and `IRunStarter.cs`:
    `ContinueQuestion`, `CameraStart` and `CommandRunner` implement them).
  - `Application/Settings`: the options classes (`RecordingOptions.cs` with `KeepScreenOn` and `ReopenWindow`,
    `ElevenLabsOptions` with `ApiKeyVariable`, `TranscriptOptions`, `YouTubeOptions` with `IsChannel`; `DriveOptions` is
    with Drive), the catalog (`SettingsCatalog.cs`: `SettingsCatalog`, `SettingKey`, `SettingKind`; `DefaultSettings`, the
    Logging defaults; `DefaultChanges.cs`: `DefaultChanges`, `DefaultChange`, the rows a release fills), the saved classes'
    choices (`ClassChoices.cs`: `ClassChoices`, `SourceGiven`, `SourceOption`), a settings file as the use cases see it
    (`SettingsText`, `SettingsOrigin.cs` with `SettingsOrigin` and `SettingsSource`, `SettingsVerdict.cs` with
    `SettingsVerdict` and `DroppedSetting`, `SettingsEdit.cs` with `SettingsEdit` and `SettingsChange`, and
    `SettingsDocument`, the user's file as a tree the writer changes) and the ports of slice 3b3 (`ISettingsStore`,
    `IStagedSettings`, which `StagedSettings` implements, `ISavedClasses`, which `SavedClasses` implements, and
    `IOutputFolderFacts`, the folders' facts the warnings use).
  - `Application/Storage`: the ports of slice 3b1, `IClassFolders` (course and date folders: `CreateRecording`,
    `CreateRecordingIn`, `OpenToContinue`, `FindContinuable`, `IsVideo`, `TrackLength`, `FilesIn`, `FoldersIn` and the rest
    the use cases call), `IRecordingFolder` (what is on disk of a recording: `FilesOf`, `OpenToRead`, `Delete`,
    `IsUploadCopyInUse`, `DeleteUploadCopy`, `FindSegments` per track, `FindCameraPieces`, `FindKeptAnswers`,
    `HasTranscript`, `NextFreeUnreadAnswer`), `ITranscriptCopies`, `IClassMarks`,
    `IMicCheckLog.cs` (`IMicCheckLog`, `IMicCheckLogFile`) and `IFreeSpace`; `IAudioOnDrive.cs` (`IAudioOnDrive`,
    `NoAudioOnDrive`: what the lists and commands know of audio kept on Drive alone; `ClassFolders` takes it as an optional
    last argument); and `Room` (the room recording takes on a drive: an hour of one track, an hour of the upload copy, and a
    class of a given length, for a run's space warnings and a YouTube batch's reserve).
  - `Application/Transcription`: the ports `ITranscriptionClient.cs` (`ITranscriptionClient`, `TranscriptionException`),
    `IElevenLabsAccount.cs` (`IElevenLabsAccount`, `AccountReading`, `AccountReadingKind`), `IGlossaryFiles.cs`
    (`IGlossaryFiles`, `GlossaryText`) and `ITranscriptAnswers` (the saved answers, for `rebuild`); `GlossaryChoice` (the
    one resolver that decides a recording's glossary, scoped), the transcript formatter (`TranscriptFormatter`, with
    `SourceLine` for a video), the workflow (`TranscriptionWorkflow`, rebuild included, `VideoOf`, `RefuseIfTooLong`),
    `TranscriptionProgress.cs` (`TranscriptionProgress`, scoped: the workflow and the client report preparing, uploading at
    a percent and waiting, which `RecordCommand` shows as UPLOADING 40% and writes into the run file's heartbeat, which the
    menu header and `list` read; `TranscriptionStep`), `ProgressReadStream`, the upload's time limit (`UploadTimeLimit`,
    pure: the audio's length comes from `IAudioFileFacts`), what one file holds (`TranscriptionLimits`), and what a
    transcript cost: `TranscriptionCost` (the estimate) and `UsageReport` (scoped: the read before the upload and the line
    after; `Describe` for a YouTube plan, `Charged` for its projection), and `TranscriptionUseCases`
    (`AddTranscriptionUseCases`).
  - `Application/YouTube`: the shared parts `YouTubeLink.cs` (`YouTubeLink`, `YouTubeLinkKind`, `LinkReading`,
    `YouTubeLinks`), `LinkWord`, `YouTubeGuards` (the test guard's switch), `YouTubeRequest.cs` (`YouTubeRequest`,
    `BatchConfirmation`), `YouTubeBatch.cs` (`YouTubeBatch`, `BatchKeys`), `BatchPace.cs` (`IBatchPace`, `BatchPace`,
    `ClassWait`), `PastVideo.cs` (`PastVideo`, `PastKind`, `PastVerdict`), `VideoTranscripts.cs` (`VideoToTranscribe`,
    `IVideoTranscriber`, `ScopedVideoTranscriber`, `VideoTranscriptRun`, `TranscriptOutcome`, `TranscriptStep`,
    `VideoTranscribed`, `BatchTranscripts`), `TranscriptionRuns.cs` (`TranscriptionRuns`, `TranscriptionRun`) and the ports
    `IVideoIndex`, `IVideoSources`, `IYouTubeFolders` and `IYouTubeLock`; the download path `IVideoTool.cs` (`IVideoTool`,
    `VideoInfo`, `ListedPlaylist`, `ListedVideo`), `IToolLocator` (with `BrowserNotFound`), `ToolPaths`, `IToolRunner.cs`
    (`ToolCall`, `ToolLine`, `ToolResult`, `IToolRunner`), `ToolCheck`, `DownloadFailure.cs` (`DownloadFailureKind`,
    `DownloadFailure`), `DownloadGuard.cs` (`GuardVerdictKind`, `GuardVerdict`, `DownloadGuard`), `DownloadPlan.cs`
    (`BatchVideo`, `PlanRoom`, `DownloadPlan`), `IncompleteAudioException`, `IDownloadedAudio` and `DownloadBatch`; the
    record path `VideoTitle` (with `NoTitle`), `IVideoTitles`, `OpenedWindow.cs` (`OpenedWindow`, `WindowOpening`),
    `VideoTitles` (`Plain`, `Clean`), `IVideoWindowOpener`, `VideoPlayback`, `VideoEnds.cs` (`VideoEnds`,
    `VideoWindowClosedException`) and `VideoBatch.cs` (`RecordedVideo`, `VideoPlan`, `VideoBatch`); and `YouTubeUseCases`
    (`AddYouTubeUseCases`).
- `src/ClassScribe.Infrastructure` (namespace `ClassScribe.Infrastructure`): the adapters of the ports, with Windows,
  files, processes, pipes and web APIs; the vendors' records and protocols; the JSON contexts (`ElevenLabsJson`,
  `RecorderJson`, `LinkJson`, `CameraJson`, `DriveJson`, `DriveStoreJson`, `GoogleOAuthJson`, `UploadJson`, `YouTubeJson`,
  `YtDlpJson`, `GlossaryJson`) and Refit's generated clients, since this is the one project that sends requests and reads
  or writes JSON of its own types. At its root `InfrastructureServices` (`AddClassScribeInfrastructure(configuration)`:
  each area's `<Area>Services`, see Conventions).
  - `Infrastructure/Audio`: WAV writing (`WavSegmentWriter`: each packet goes to Windows at once; the header is rewritten
    and flushed to disk every `Recording:FlushSeconds`; and `WavTrackWriterFactory`, the `ITrackWriterFactory` over it, an
    `ITrackWriter`), `AudioFileFacts` (the `IAudioFileFacts`: a WAV file's length from its header and its size on disk),
    upload preparation (`AudioPreparer.cs`: `AudioPreparer`, the `IAudioPreparer`, with `MixedSound`, `FiniteSamples`,
    `MonoDownmix`, `SincResampler` and `WaveData`: the microphone's track mixed in, 16 kHz mono) and `AudioServices.cs`
    (`AddAudio`, and `LazyLoopbackCaptureProvider`: `ILoopbackCaptureProvider` is made on first use, so the app,
    `transcribe`, `rebuild` and `list` never touch Windows audio, and `Lazy<ILoopbackCaptureProvider>` (for `devices`)
    resolves the registered provider).
  - `Infrastructure/Audio/Microphone` (namespace `ClassScribe.Infrastructure.Audio.Microphones`): `MicrophoneServices.cs`
    (`AddMicrophone`, called from `InfrastructureServices.AddClassScribeInfrastructure`: the capture factory
    `MicrophoneActivation`, the provider behind `LazyMicrophoneProvider`, declared here, the sessions reader, and the
    `Lazy<>`s for `devices`).
  - `Infrastructure/Audio/Windows`: `CoreAudioInterop.cs` holds ClassScribe's own Core Audio declarations (the property
    store and its readers, `PropertyKeys`, `EndpointFormFactor`, the endpoint volume and session interfaces) and
    `CoreAudio` (`CreateEnumerator`, `OpenPropertyStore`, `Check`, `Release`): the device check and the microphone listing
    go through them, and NAudio does the notifications, the output device names and the captures. `WasapiAudioCapture.cs`
    is the one capture class for devices, apps and microphones (`ForOutputDevice`, `ForApp`, `ForMicrophone`; the capture
    thread is "ClassScribe capture" or "ClassScribe microphone"; packets through an internal `IPacketSource` seam, stamped
    with their QPC position); `AudioClient2Interop.cs` (ClassScribe's `IAudioClient2`: `IAudioClient2Native`,
    `AudioClient2`, `AudioClientPropertiesNative`) and `MicrophoneActivation.cs` (the microphone capture factory and
    Windows' activator, `WindowsAudioClientActivator` behind `IAudioClientActivator`, RAW with the fallback;
    `ActivatedClient`); `WasapiMicrophoneProvider.cs` (the microphone listing, notifications, mute) and `EndpointFacts.cs`
    (what a capture endpoint's property store says: the array, Bluetooth and loopback rules, the container id);
    `WasapiMicrophoneSessions.cs` (the sessions and mute per capture endpoint, with `IAudioSessionControl2WholeRaw`);
    `WasapiLoopbackCaptureProvider.cs`, `ProcessLoopback.cs` and `CaptureGapWatch.cs` (`CaptureGapWatch`, `CaptureGap`) as
    before.
  - `Infrastructure/Camera`: `CameraThread.cs` (the camera process's own STA thread), `CameraJobs.cs` (the hold and check
    jobs, `CameraHoldJob` and `CameraCheckJob`), `CameraRecordJob.cs` (the record job), `CameraFinishJob.cs` (the finish
    job: the lesson's video, over `ThisProgram.Current` for the command it prints), `ILessonVideoMedia` (the seam between
    the finish job and Media Foundation), `CameraLines` (the lines of `CameraProtocol` as a starter sends and reads them),
    `CameraLink` (the `ICameraLink`: an adapter over `CameraLines` and a `LineChannel`), `CameraJson` (the camera process's
    lines, camelCase, choices by name, strict as the link reads its messages), `JoinedStream` (one stream over two, so a
    `LineChannel` speaks over a pair of one-way pipes; public, since `CameraHost` makes it), `WebcamHold.cs` (`WebcamHold`,
    `WebcamHolds`: the `IWebcamHolds` and `IWebcamHold`) and `CameraServices` (`AddCamera`, after `AddMicrophone`).
  - `Infrastructure/Camera/Windows`: `WindowsCameraCapture.cs` (the real MediaCapture, with `InitializeShared`, which
    refuses in a test, and `WindowsCameraCaptureFactory`), `WindowsCameraFrame.cs` (a frame's pixels copied out,
    `MemoryBufferBytes`), `MediaFoundationPieceWriter.cs` (the piece writer and its factory, `VideoGuids`,
    `FileStreamAsIStream`), `MediaFoundationLessonVideo.cs` (the lesson video's media work, `H264Pictures`,
    `LessonVideoGuids`), `WindowsCameraProvider.cs` (WinRT's `DeviceInformation.FindAllAsync` with
    `System.Devices.ContainerId` and `MediaFrameSourceGroup.FindAllAsync`, which open nothing; Windows older than
    10.0.19041 and a list Windows will not give, whatever exception WinRT maps its HRESULT to, answer with a `CameraList`
    that says why, and only the caller's own cancel is thrown; an internal constructor takes a stand-in read,
    `WindowsCameraProviderFailureTests`), `WindowsCameraSystem.cs` (`WindowsCameraSystem`: Windows' version, whether an
    H.264 encoder is installed, and the antivirus programs Security Center knows of; `CameraUsageRecord`, the ConsentStore
    webcam key), `SecurityCenterInterop.cs` (`IWscProductListRaw`, `IWscProductRaw`: Security Center's product list through
    ClassScribe's own declarations, since Windows registers no type library) and `CameraProcessStarter` (the camera process
    as a `PipedProcess`). The microphone's container is read by `EndpointFacts` (`PropertyKeys.ContainerId`,
    PKEY_Device_ContainerId) and carried as `Microphone.ContainerId`. `devices` lists the cameras after the microphones. The
    fakes are `LaptopCameras` and `FakeCameraProvider` (`TestDoubles.Camera.cs`); the real list is the category
    `WindowsCamera` (`WindowsCameraProviderTests`, run with the suite, listing only, no light).
  - `Infrastructure/Desktop`: the adapters that need no Windows call of their own: `FileProbe` (the `IFileProbe`),
    `OwnProcesses` (the `IOwnProcesses`), `ProcessFamily` (the `IProcessFamily`, over `IProcessTree`), `WindowsDesktop`
    (the `IDesktop`: opens folders and files the Windows way, a failure logged rather than thrown), `ProcessExitWarnings`
    (the `IExitWarnings`) and `AppDomainCrashWarnings` (the `ICrashWarnings`); the helpers only adapters use:
    `ChildEnvironment.WithoutTheKey` (the environment a started process gets, never the API key) and `DeviceGuards`
    (the switch `ClassScribe.Devices.RefuseTheRealDevices`, which the camera process's real start and Windows' activation
    of a capture endpoint refuse under); and `DesktopServices` (`AddDesktop`: the power requests and source, the clocks,
    the registry values, the process liveness, the usage record, the process names, the own processes, `IThisProgram` as
    `ThisProgram.Current`, `IProcessFamily`, `IVideoWindows`, the window signals, the session events, the desktop, the exit
    and crash warnings, the process starter and the job inspector).
  - `Infrastructure/Desktop/Windows`: the Windows calls: `ProcessStarter.cs` (`ProcessStarter`, `CreateProcessW` behind
    `IProcessCreationApi` and `NativeProcessCreationApi`, with `ProcessLaunch`, `ProcessCreationFlags`,
    `ProcessWindowShow`, `StandardHandles`, and the record `ThisProgram`, the adapter of `IThisProgram`, which
    `ProcessStarter` and `CameraProcessStarter` keep using for the command line), `ToolProcesses.cs` (the tool and browser
    start on `NativeProcessCreationApi`: environment block, handle list, suspended start into `ToolJob`; any program but
    `cmd.exe` refused in tests), `PipedProcess` (a program started as ClassScribe starts its tools and its camera process:
    three pipes, no other handle, no window, a kill-on-close job of its own), `JobInspector.cs` (`IsProcessInJob` and
    `QueryInformationJobObject` behind `IJobApi`, `NativeJobApi`), `SessionWindow.cs` (with `IShutdownReasonApi`,
    `NativeShutdownReasonApi`), `ConsoleWindowSignals.cs` (`IConsoleWindowApi`, `NativeConsoleWindowApi`:
    `WindowsTerminalVariableSet`, `ClassOf`, `HasTaskbarButton`), `TaskbarList.cs` (`ITaskbarList3` declared with
    `ITaskbarList`'s and `ITaskbarList2`'s methods first, since COM interop ignores interface inheritance;
    `ITaskbarProgress`), `WindowFinder.cs` (`WindowFinder`, `IProcessTree`, `NativeProcessTree`, `ProcessFacts`),
    `WindowsPowerRequests.cs` (`WindowsPowerRequests`, `WindowsPowerSource`, `IPowerRequestApi`, `NativePowerRequestApi`,
    `PowerRequestHandle`, `PowerRequestType`), `WindowsSystemTimes`, `RegistryValues`, `ProcessNames`,
    `ConsentStoreRecord` (one of Windows' records of which apps used a device and when, under HKEY_CURRENT_USER's
    ConsentStore; every key opened for reading and closed at once) with `MicrophoneUsageRecord` (the microphone's) and
    `ChromeWindows` (`EnumWindows` by process, class `Chrome_WidgetWin_1`, `GetWindowTextW`, `WM_CLOSE`; refused in tests).
  - `Infrastructure/Drive` and `Drive/Windows`: `IGoogleOAuthApi.cs` and `IDriveApi.cs` (the Refit interfaces; `DriveApi`
    with `WrittenFor`, `DriveApiSettings`), `GoogleJson.cs` (`GoogleOAuthJson`, `DriveJson`, `DriveStoreJson` and their
    records, which print no token: `GoogleTokenAnswer`, `DriveAbout`, `DriveUser`, `DriveStorageQuota`, `DriveFile`,
    `DriveFileList`, `DriveFileMetadata`, `GoogleErrorAnswer`, `GoogleError`, `GoogleErrorItem`, `RememberedFolders`),
    `GoogleClientFile` (the `IGoogleClientFiles`: the import of a client file, `installed` only), `DpapiGoogleCredentialStore`
    (the `IGoogleCredentialStore`, DPAPI-protected for the Windows account), `GoogleTokens.cs` (`GoogleTokenCache`, one per
    process with a `SemaphoreSlim`, and `GoogleTokens`, the `IGoogleAccount`), `DriveClient` (the calls, the 401 renewal,
    Google's refusals in words and kinds, `PieceAnswer`, `PieceOutcome`, `StartRevisionAsync`), `DriveFolders` (the root
    folder and the levels under it, remembered in `drive\folders.json`), `GoogleDrive` (the `IGoogleDrive`: the thin adapter
    over `DriveClient`, `DriveFolders` and `ResumableUpload`), `ShellBrowser` (the `IBrowser`, which never logs the address),
    `PieceContent`, `ResumableUpload.cs` (`ResumableUpload`, `UploadAddress`, Google's upload path, checked against
    `Drive:ApiBaseUrl`), `UploadManifests.cs` (`UploadManifests`, the `IUploadManifests`, `drive\recordings\<base>.<8 hex>.json`,
    and `UploadJson`), `DriveUploadLock` (the `IDriveUploadLock`, `drive\upload.lock`), `DriveServices` (`AddDrive`:
    Google's clients and the adapters, called from `InfrastructureServices` after the transcription's) and
    `Windows/HttpListenerLoopbackReceiver.cs` (the `ILoopbackReceiver` on `http://127.0.0.1:<free port>/`, five tries).
  - `Infrastructure/Link`: one named pipe per run: `LinkMessages.cs` (`LinkProtocol`, `LinkTypes` and `LinkKeys`, and the
    messages `Envelope`, `Hello`, `Welcome`, `KeyPress`, `StateUpdate`, `LogLine`, `End`, `Bye`, `ByeReasons`, `Refused`),
    `LineChannel`, `RecorderServer`, `RecorderClient.cs` (`RecorderClient`, `LinkFailure`, `LinkLoss`, `LinkLost`,
    `Reconnected`, `RecorderSilent`, `RecorderLinkException`) and `LinkJson`; messages are lines of JSON, and a field added
    to a record that travels on it needs a default. The link's own messages stay with the link; the records it carries
    (`RunRecord`, `RecorderState`) are Core's, and the version number is `LinkVersion` in Core's `Runs`.
  - `Infrastructure/Logging`: `PlainConsoleFormatter` (the window's console log and `RecorderLogProvider` both use it).
  - `Infrastructure/Recorder`: the separate recorder's files, lock and log: `RuntimeFolder` (`%LOCALAPPDATA%\ClassScribe`;
    the `IRuntimeFolder`; `RealRoot()`, `RefuseTheRealFolderSwitch`), `RecordingLock` (the `IRecordingLock`),
    `RunJournal.cs` (`RunJournal` and `RunHandle`, the `IRunJournal` and `IRunHandle`: run and request files, `MarkSeen`,
    `MarkSeenIfOver`, prune; the port's `Begin` is explicit over the adapter's, which gives a `RunHandle`), `RecorderJson`,
    `ProcessLiveness` (the `IProcessLiveness`), `StopSignal.cs` (`StopSignal`, with the yield event: `YieldPrefix`,
    `ListenForYield`, `TrySetYield`, `YieldNameFor`; and `WindowsVideoYield`, the `IVideoYield`), `RecorderLog.cs`
    (`RecorderLog`, the `IRunLog`: the ring of 20,000 lines, `Written`, the files, and `LastStartupLines`, the start-up log's
    last lines a window reads), `RecorderLogProvider` and `RecorderServices.cs` (`AddRecorderLog`: the log and the logging
    provider that writes into it).
  - `Infrastructure/Settings`: `ApiKeyFromEnvironment.cs` (the key from `ELEVENLABS_API_KEY`, the last configuration
    source; `ApiKeyFromEnvironmentExtensions`), `SettingsCheck` (types, enums, ranges and paths, lists, the Logging section,
    the warnings, keyed problems for the recording's drop; `CheckYouTube`, `CheckExe`, `CheckLocalOrHttpsAddress`,
    `FolderWarnings`), `OutputFolderFacts` (the `IOutputFolderFacts`), the places (`SettingsPlaces`: the user's folder, the
    program's, the marker, the Missing state; `ForThisUser`), the reload (`SettingsFile` with `DescribeStaleCopy`,
    `CheckedSettingsSource`, `SettingsReloader` with `Snapshot`, `StagedSettings`, the `IStagedSettings`), the writer
    (`SettingsWriter`), `SettingsStore` (the `ISettingsStore`: the thin adapter over the places, the file, the reloader and
    the writer), `SavedClasses` (the `ISavedClasses`), `SettingPaths` (a path setting as the adapters use it, `Expanded`)
    and `SettingsServices` (`AddSettings`, and `AddSettingsReload`, which `AppHost` calls).
  - `Infrastructure/Storage`: `ClassFolders` (the `IClassFolders` of the use cases: course and date folders, the old flat
    layout read too; `CreateRecordingIn` and `IsVideo` for YouTube; `TrackLength`, `FilesIn` and `FoldersIn` for the use
    cases; `RootFor`, `Root` and `RootNamedBy`, with `RefuseTheRealFolderSwitch`; its disk statics stay static for
    Infrastructure's own callers), `RecordingFolder` (the `IRecordingFolder`: what is on disk of a recording),
    `TranscriptCopies` (the `ITranscriptCopies`: numbered transcript copies, never overwritten, each copy's `.json` and `.md`
    claimed with `FileMode.CreateNew`), `ClassMarks` (the `IClassMarks`: marks and notes, `AddResume`), the microphone
    check's log file (`MicCheckLog` and `MicCheckLogFile`, the `IMicCheckLog` and `IMicCheckLogFile`: the header, the cost
    line, flushed per line; no `Z` in its stamp, so `RecordingFiles.TryParse` never takes one for a recording), `FreeSpace`
    (the `IFreeSpace`), `RoamingFolder` (`%APPDATA%\ClassScribe`, where `google-drive.dat` is kept; tests give a temporary
    one; `RefuseTheRealFolderSwitch`) and `StorageServices` (`AddStorage`: the storage services, each one for the process,
    and ClassScribe's own folder, the lock and the run journal with their ports).
  - `Infrastructure/Transcription`: the ElevenLabs client (`IElevenLabsApi.cs`: the Refit interface, `ElevenLabsApi` with
    `WrittenFor`, `ElevenLabsApiSettings`; `ElevenLabsClient`, the `ITranscriptionClient`, around it; `ElevenLabsJson.cs`:
    `ElevenLabsJson`, `SpeechToTextResponse`, `SpeechToTextWord`, `SubscriptionResponse`), `ElevenLabsAccount` (the
    `IElevenLabsAccount`, the reader of `GET /{apiVersion}/user/subscription`), the shared answer parser (`ElevenLabsAnswer`,
    the `ITranscriptAnswers` of the saved answers, which must keep reading pretty-printed answers), `GlossaryFiles.cs`
    (`GlossaryFiles`, the `IGlossaryFiles`: a glossary's lines and a recording's glossary record; `GlossaryJson`) and
    `TranscriptionServices` (`AddTranscription`).
  - `Infrastructure/YouTube`: `LinkList.cs` (`LinkList`, `LinkListReading`: UTF-8 list files, `#` comments, duplicates
    by ID taken once), `VideoSourceFile.cs` (`VideoSourceFile`, the `IVideoSources`, written atomically, unknown fields
    passed over; `YouTubeJson`), `YouTubeFolders` (the `IYouTubeFolders`: every YouTube folder derives from
    `RuntimeFolder`: `youtube\` with the lock, `converting.json`, `staging\<ID>\`, `yt-dlp-cache\`, and `video-browser\`,
    named only so that nothing else goes in it), `YouTubeLock` (the `IYouTubeLock`, `youtube.lock` with the holder's pid),
    `VideoIndex` (the `IVideoIndex`, scoped), the download path `YtDlpTool` (the `IVideoTool`, scoped), `ToolLocator.cs`
    (`ToolLocator`, the `IToolLocator` of the tools: `YouTube:YtDlpPath`, winget's links folder, then PATH from the
    registry; `ToolSearch` with `Missing`), `WindowsCommandLine` (the quoting of a Windows command line, for
    `ProcessStarter` and `ProcessToolRunner`), `ProcessToolRunner` (the `IToolRunner`, on `IProcessCreationApi`, never
    `System.Diagnostics.Process`; refuses every tool in tests), `YtDlpArguments` (every list, the fixed set first, `ProblemWith`
    and `Forbidden`), `YtDlpRead.cs` (`YtDlpInfo`, `YtDlpPlaylist`, `YtDlpPlaylistEntry`, `YtDlpFormat`, `PlaylistListing`,
    `YtDlpRead` and their JSON context `YtDlpJson`), `YtDlpErrors` (`Classify`), `YtDlpCheck` (each tool's version and age),
    `DownloadedAudio.cs` (`DownloadedAudio`, the `IDownloadedAudio`: NAudio's reader into ClassScribe's own `.wav`,
    `CleanUp`, `Convert`; `ConversionNote`); the record path `BrowserLocator` (the `IToolLocator` of the browser's search:
    `YouTube:BrowserPath`, App Paths under HKLM then HKCU, then the program folders), `OEmbedTitles.cs` (`IYouTubeOEmbedApi`,
    the Refit interface; `OEmbedAnswer`; `OEmbedTitles`, the `IVideoTitles`; `YouTubeHostGuard`, which refuses YouTube's
    hosts in tests) and `VideoWindow` (the `IVideoWindowOpener`: ClassScribe's own Chrome window, `Arguments`, `HandedOff`);
    and `YouTubeServices` (`AddYouTube`, its adapters and the oEmbed client; `IFileProbe` is registered here, where it was
    first registered).
- `src/ClassScribe.Cli` (namespace `ClassScribe.Cli`; assembly `ClassScribe`, so the build makes `ClassScribe.exe` and
  `ClassScribe.dll`): it talks to the person and composes the rest. `Program.cs` (top-level statements: `--recorder` to
  `RecorderHost` and `--camera-process` to `CameraHost`, both before anything touches the console; then the console set to
  UTF-16, and to UTF-8 when redirected for `settings` and `class`; `AppHost.Build` and `App`; a start-up failure printed
  plainly, exit 1; the close prompt), `AppHost` (builds the host: the checked settings source and a first checked read,
  the key from the environment, the plain log format, scopes and registrations validated; `BuildRecorder` with
  `AddRecorder`, the recorder's own services in place of the window's; `LogToStandardError` for `--json`;
  `DescribeFailure`), `App` (the command line, with the gate before each command, and the report at the start for
  commands), `ServiceCollectionExtensions` (`AddClassScribe`: the clock, Application, Infrastructure, `CommandServices` and
  `WindowServices`, see Conventions) and `appsettings.json` (the shipped defaults, copied beside the exe; the one
  `appsettings.json` of the four projects).
  - `Cli/Commands`: the menu (`MenuCommand`; `MicrophoneMenu.cs`, menu choice 7: W, I or N, the microphone, the follow
    question; `DriveMenu`, menu choice 9; `YouTubeMenu.cs`, `YouTubeMenu` for menu choice Y and `YouTubeMenuMemory`;
    `ClassesMenu` and `SettingsMenu`, menus C and S, per run; `RecordingNamePrompt`; `ClosePrompt`), the command line
    (`CommandLineTree` on System.CommandLine, one partial class in `CommandLineTree.cs`, `CommandLineTree.Settings.cs` and
    `CommandLineTree.Classes.cs`, with `CommandKind` and `ParseOutcome`: `Tidy`, `Parse` with ClassScribe's own errors and
    exit 2, `KindOf`, `InvokeAsync` with Ctrl+C left to ClassScribe, `YouTubeProblems`, `IsOwnKind`, `WantsJson`),
    `ICommandRunner.cs` (`ICommandRunner`, which extends `IRunStarter`, and `CommandRunner`: each command in its own DI
    scope, record through `IRecordStarter`, attach through `AttachCommand`, list, devices, the microphone menu and the
    check through their commands; `TranscribeAsync` refuses a recording a live run records or transcribes), the console
    (`ConsoleUi`, the `IConsoleUi`, with `UseUnicode`, `UseUtf8WhenRedirected` and `OwnsConsoleWindow`;
    `ConsoleUserSignals`, the `IUserSignals`), the view of a background run (`RecorderView.cs`: `RecorderView`,
    `ConsoleCtrlC`, `WindowText`; `AttachCommand`), the report at the start (`StartupReport`, the `IStartupReport`, which
    replaces `NoStartupReport`), the gate (`RunGate.cs`: `RunGate`, `GateUse`), the window's prompts (`ContinueQuestion`, the
    `IContinuePrompt`; `CameraStart`, the `ICameraPrompt`), `CommandOutput` (where `settings` and `class` write: standard
    output and standard error) with `SettingsOutput` and `ClassOutput` (the text and `--json` of the two commands,
    architecture question 4), and the registrations `CommandServices.cs` (`AddCommands`) and `WindowServices.cs`
    (`AddWindow`, the window's side of a run in the background). A start from the window the recorder opened by itself is
    `StartOrigin.AutoContinueFromAlert`: shown with the keys locked, never recorded in that window, never marked seen.
  - `Cli/Desktop`: `WindowStatusSink` (the `IRecorderStatusSink` of a run in this window: the title and the taskbar button
    as `TitleText` gives them, and a flash for each new Alert).
  - `Cli/Hosts/Recorder` (namespace `ClassScribe.Cli.Hosts.Recorder`): `RecorderHost.cs` (`RecorderHost`: the recorder
    process from the request to the end, with `IsRecorderStart` for `Program`, the exit codes, `EndPatience`, and the seams
    `Folder`, `Places`, `Time` and `ConfigureServices`) and the four adapters only the recorder's host registers:
    `LinkConsoleUi` (the windows' keys as the recorder's console), `LinkStatusSink` (states to the windows and the alerts,
    repeated once a second), `RecorderResultShower` and `UnattendedAlerts` (the minimized window brought back by itself; a
    window that left with Esc counts as no close).
  - `Cli/Hosts/Camera`: `CameraHost` (the camera process, `ClassScribe.exe --camera-process <job id>`: it reads its job,
    the first line on its standard input, speaks `CameraProtocol`'s lines on its standard output, and runs the hold, check,
    record or finish job on an STA thread of its own through `CameraRoute`).
- `tools/dry-run`: the dry-run kit (its `README.md`): `Start-DryRun.cmd` and `Start-DryRun.ps1` (dot-sourced, it
  defines its functions and runs nothing; `Invoke-DryRun -Roots` takes the folders, `Get-DryRunRoots` gives this
  user's; `Get-ClassScribeProject` finds the project file of either layout; `Get-MissingPublishFiles`, `Switch-Install`
  and `Test-Installed` are the class launcher's functions, tested here; `Test-BuildReadsSettingsFolder` looks for the
  settings folder's variable in every `ClassScribe*.dll`), `ElevenLabsStub.cs` (the stand-in for ElevenLabs: `SoundFinder`, `StandInTranscript`, `StandInUpload`,
  `StandInServer`, `StandInProgram`, in a block namespace `ClassScribe.DryRun`), `TestPlayer\` (plays a clip aloud as the
  class, only with `-Player`; not in the solution) and a `.gitattributes` that keeps `.cmd` and `.ps1` in CRLF.
- `examples/glossary`: the example glossary of the Islamic sciences (`keyterms-islamic-sciences.txt`, with a `README.md`),
  never built or published; only the test project links it, for `GlossaryFilesTests`.
- `tests/ClassScribe.Tests`: NUnit and Moq, one project for all four (architecture question 6; `net10.0-windows10.0.19041.0`,
  referencing `ClassScribe.Cli`, the rest coming with it; the namespace `ClassScribe.Tests`). `Architecture/` holds the
  architecture test (`ArchitectureFacts`, `ArchitectureRules`, `ArchitectureRulesTests`, `ArchitectureTests`,
  `AppAssemblies`, `RepositoryLayoutTests`; see the rule of dependencies under Conventions). Hand-written fakes for the device, keyboard, clock and
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
  `TestDoubles.View.cs` (`FakeCtrlC`, `FakeStopRequests`), `TestDoubles.CommandLine.cs`,
  `TestDoubles.Capture.cs` (`FakeAudioClient` over NAudio's public `IAudioClient`,
  `FakeAudioClient2`, `FakePacketSource`, `FakeAudioClientActivator`), `TestDoubles.Timeline.cs`
  (`FakePacketGate`, `JumpingClock`), `TestDoubles.Microphone.cs` (`LaptopMicrophones`,
  `FakeMicrophoneProvider`, `FakeRegistryValues`), `TestDoubles.MicrophoneListing.cs` (the fake
  enumerator, endpoints, stores and endpoint volumes, and `LaptopEndpoints`),
  `TestDoubles.MicrophoneGate.cs` (the `Laptop` fixture, `FakeMicrophoneSessions`,
  `FakeMicrophoneUsageRecord`, `FakeProcessNames`, `FakeOwnProcesses`, `ScriptedLooks`, and the
  COM-level fakes of the capture endpoints), `TestDoubles.MicrophoneRun.cs`
  (`FakeMicrophoneRecording`, `FakeMicrophoneRecordingFactory`), `TestDoubles.TrackSet.cs`
  (`TestLoggerFactory`), `TestDoubles.Account.cs` (`FakeElevenLabsAccount`), `TestDoubles.DryRun.cs`
  (`WindowsPowerShell`, a hidden Windows PowerShell 5.1 for the kit's tests) and `TestDoubles.Storage.cs` (`OnDisk`: a
  recording's files read through the real `RecordingFolder`, where a test looks at what it wrote). The ports of slice 3b1 are
  tested against their real adapters over a `TempFolder` in `FileProbeTests`, `ClassFoldersPortTests`, `RecordingFolderTests`,
  `StoragePortTests` (the marks, the copies and the check's log), `WavTrackWriterFactoryTests` and `AudioPortTests` (the file
  facts, the saved answers and the capture's processing); the ports of runs of slice 3b2 in `RecorderPortTests` (the folder,
  the lock, the journal and the handle with a run file pinned to `RecorderJson`'s bytes and the journal's retries, the log
  with the copy into the recording's log and the start-up log's last lines (none for a count of 0 or less), the log the
  recorder's host is given left to that host, never disposed by the container, and the ports' registrations in both hosts),
  over a `TestRuntime` and a `TempFolder`; the ports of settings and classes of slice 3b3 in `SettingsPortTests` (the places'
  facts before and after the first change and the missing-file state, the file read and staged with a value by name, a
  section's children and the classes, bytes that are not a settings file, every change through the one writer, the
  override, the snapshot and a file a setting names, the saved classes in force and in the file, and the ports'
  registrations in both hosts), over a `SettingsFileFixtures`, which gains `Store` as `TestSettings` does; the ports of the
  program and the prompts of slice 3b4 in `ProgramPortTests` (this process's facts over the real `ThisProgram`, the command
  lines over it against the explicit forms, and the registrations of the port and `CommandLine` in both hosts) and
  `PromptPortTests` (the continue prompt over the real `ContinueQuestion` in temporary folders, the camera prompt over the
  real `CameraStart` and the camera's fakes, the run starter over the real `CommandRunner` with a played starter in each
  scope, and the registrations in both hosts); the expected command lines in every test are `Printed`'s (`TestDoubles.cs`:
  `CommandLine` over the real `ThisProgram`, `Printed.Program`, which the subjects are given too). The cost line's
  tests are `TranscriptionCostTests`, `ElevenLabsAccountTests` (on `StubHttpHandler`),
  `UsageReportTests`, and the order checks in `TranscriptionWorkflowTests`, `RecordCommandTests`,
  `TranscribeCommandTests` and `EndToEndTests`; the Bluetooth watch's are
  `BluetoothMicrophoneWatchTests` and the monitor's in `RecordingMonitorTests`. The microphone's tests: `TrackSetTests`, `RecordingSessionTimelineTests`,
  `MicrophoneGateTests`, `ClassMicrophoneWatchTests`, `MicrophoneChooserTests`, `MicrophoneAdviceTests`,
  `MicrophoneSourceTests`, `MicrophoneActivationTests`, `MicrophoneMenuTests`, `MicCheckCommandTests`,
  `MicrophoneChangeTrackerTests`, `MicCheckLogTests`, `UploadMixTests`, `MediaTimeTests`,
  `RecordingClockTests`, `TrackPlacerTests`, `EndpointFactsTests`, `CoreAudioPropertyTests` and
  `RecorderSoakTests`. The course > date layout's tests are `ClassFoldersLayoutTests` (on a
  `ManualTimeProvider` read through `ZonedClock` in a fixed zone, so the local date is the same on
  every machine), with layout cases in `ClassFoldersTests`, `ListCommandTests`, `MenuCommandTests`,
  `TranscribeCommandTests`, `RebuildCommandTests`, `RecordCommandTests` (continue in either layout,
  a class past midnight), `ContinueQuestionTests`, `StartupReportTests` and `EndToEndTests`; a test
  that looks for a new recording's folder finds the one date folder rather than working out today's
  date, so a run across midnight passes. YouTube's tests (fakes in `TestDoubles.YouTube.cs`,
  `TestDoubles.VideoWindows.cs` and `TestDoubles.VideoBatch.cs`): `YouTubeLinkTests`,
  `LinkListFileTests`, `YouTubeCommandLineTests`, `YouTubeMenuTests`, `YouTubeMenuRecordTests`,
  `YouTubeSettingsCheckTests`, `YouTubeLockTests`, `ClassFoldersVideoTests`, `VideoSourceFileTests`,
  `VideoTranscriptFormatterTests`, `VideoTranscriptionWorkflowTests`, `VideoTranscriptRunTests`,
  `TranscriptionRunsTests`, `VideoRunRecordTests`, `VideoDriveTests`, `DriveRoomTests`,
  `ListCommandVideoTests` (in `ListCommandTests.cs`), `BatchKeysTests`, `BatchPaceTests`,
  `ToolLocatorTests`, `YtDlpCheckTests`, `YtDlpArgumentsTests`, `YtDlpReadTests`, `YtDlpErrorsTests`,
  `DownloadGuardTests`, `DownloadPlanTests`, `DownloadedAudioTests`, `DownloadBatchTests`,
  `ProcessToolRunnerTests`, `BrowserLocatorTests`, `BrowserStartTests`, `OEmbedTitlesTests`,
  `VideoWindowTests`, `ChromeWindowsGuardTests`, `VideoPlaybackTests`, `VideoRecordingMonitorTests`,
  `VideoBatchTests`, `VideoContinuationTests`, the partials `RecordCommandTests.Video.cs`,
  `RecordStarterTests.Video.cs` and `RecorderHostTests.Video.cs`, and additions to `StopSignalTests`,
  `StartupReportTests`, `ContinueQuestionTests`, `RecordingFilesTests`, `RegistryValuesTests`,
  `ServiceRegistrationTests` and `EndToEndTests`; the contract tests of slice 3c's ports are `DrivePortTests`,
  `YouTubePortTests` and `CameraPortTests`. The glossary's tests: `KeytermRulesTests`,
  `GlossaryFilesTests` (the example glossary, which the test project links into its output as
  `Examples\keyterms-islamic-sciences.txt`), `GlossaryChoiceTests`, `GlossaryRecordTests` (with
  `GlossaryRecordTests.Reuse.cs`, and `GlossaryRecordTests.Recorder.cs` for a recorder's resolver,
  which has no saved classes) and `Architecture/RepositoryLayoutTests`, with additions to
  `RecordingFilesTests`, `SavedClassesTests`, `ClassCommandTests`, `ClassCommandLineTests`,
  `ClassesMenuTests`, `CommandLineTreeTests`, `RequestsTests`, `RecordStarterTests`,
  `RecordCommandTests`, `RecorderHostTests`, `TranscriptionWorkflowTests`, `DefaultChangesTests`,
  `MenuSettingsTests`, `DownloadBatchTests`, `VideoBatchTests` and `YouTubeMenuTests` (the plans' prices
  with a course's glossary), `TranscribeCommandTests` (`--keyterms` reaching the upload),
  `RebuildCommandTests`, `DryRunScriptTests` and `DryRunWholeTests`;
  `TestGlossaries.Choice` builds the resolver for tests that build the workflow by hand (over
  `FailingGlossaryFiles` for an error nobody expects), and `TestGlossaries.Classes` the saved classes. The vendors'
  values' tests (architecture slice 2): `TranscriptionLimitsTests`, and additions to `SettingsCheckTests` (each new
  setting's range and warning, the API versions, `ElevenLabs:BaseUrl`, the safety stop held and the file limit lowered to 5
  hours refusing nothing), `YouTubeSettingsCheckTests`, `SettingsCatalogTests`, `DefaultChangesTests` (a vendor's default
  that moves needs a row), `KeytermRulesTests`, `GlossaryChoiceTests`, `TranscriptionWorkflowTests`, `RecordingMonitorTests`,
  `VideoRecordingMonitorTests`, `VideoBatchTests`, `ClassFoldersTests`, `DownloadGuardTests`, `GoogleSignInTests`
  (`FakeGoogle.AuthorizationPage`), `ResumableUploadTests` (`UploadAddress` with the version), and the routes the
  versions make over `StubHttpHandler` in `ElevenLabsClientTests`, `ElevenLabsAccountTests` and `DriveClientTests`
  (`StubHttpHandler.Enqueue(Func<HttpResponseMessage>)` for an answer with a Location). `Denied.cs` holds ACL helpers; `ClassScribe.Tests.runsettings` holds the hang
  guard; `TestGuards.cs` holds the `[SetUpFixture]` that turns the test switches on, and `RecordingsFolderGuardTests`. The
  test project sees the internals of all four projects through the `InternalsVisibleTo` item in `src/Directory.Build.props`.
