# ClassScribe

A .NET 10 console app for personal study. During an online class it records whatever the laptop
plays, by WASAPI loopback: the output device the class plays on, a device pinned by name, or one
app's sound through process loopback. It can record the user's microphone beside the class, as a
second track on the same clock, written only while the class app is listening on that microphone
and Windows is not muting it, and mixed into the class's track at upload; or the microphone alone,
for a class in a room, and then, for a lesson video, the camera's picture beside it, shared, into pieces on the same
clock, written by a camera process of its own, and joined after the stop with the sound into the lesson's finished video,
`<base>.mp4`, by another camera process's finish job. The recording runs in a separate recorder process
(`ClassScribe.exe --recorder <run id>`), which the window shows through a named pipe, so closing
the window does not end a class. When stopped, it sends the recording to ElevenLabs Scribe v2 with
a glossary of Arabic terms and saves a Markdown transcript and ElevenLabs' JSON answer beside the
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
  copy whose MD5 check failed), and, as teach-video 8 asks, a new revision of its own verified copy of a
  lesson's video (`<base>.mp4`) made again, larger or smaller; never of a `.wav`. Deleting local files,
  by `drive free-space` or by hand, never touches Drive. A test run turns on the AppContext switches
  `RoamingFolder.RefuseTheRealFolderSwitch` and `RuntimeFolder.RefuseTheRealFolderSwitch` (`TestGuards`,
  the assembly's `[SetUpFixture]`), so a test that asks for the real `%APPDATA%\ClassScribe` or
  `%LOCALAPPDATA%\ClassScribe` fails at once: with Gulzar signed in, a class recorded through the real
  app would otherwise go up to his own Drive after it ends, and the real run files and Drive queue are
  his. Every test that builds the app or the recorder's host registers `new RoamingFolder(<temporary
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
  a temporary folder first (the menu then names it), and tests use `SettingsFileFixtures`.

## Conventions

- .NET 10 and C# 14, nullable on, file-scoped namespaces, primary constructors, records and
  `init` for immutable data, target-typed `new`, collection expressions, LINQ where it reads
  well, async/await, the built-in DI container. Both projects target `net10.0-windows10.0.19041.0`
  (teach-video 6.3; Windows 10 version 2004 or later), for Windows' camera API (WinRT: the device lists and, from
  0.6.0, `MediaCapture`); the test project must follow or NuGet refuses the reference (NU1201). NAudio still takes its
  `netstandard2.0` library, and CsWinRT's `ComWrappers` sits beside NAudio's and ClassScribe's built-in COM. The
  published folder grew from about 7 to 33 MB (`Microsoft.Windows.SDK.NET.dll` is 25 MB); trimming and Native AOT stay off.
  In a namespace that ends in `Windows` (`ClassScribe.Audio.Windows`, `ClassScribe.Desktop.Windows`), WinRT's own namespaces
  need `global::Windows`.
- HTTP APIs are called through Refit interfaces registered with `AddRefitGeneratedClient` (Refit's
  source-generated client, given the `ElevenLabsJson` System.Text.Json context), not requests built
  by hand with `HttpClient`. Google's are `IGoogleOAuthApi` (`GoogleOAuthJson`, snake_case: token and
  revoke, URL-encoded forms) and `IDriveApi` (`DriveJson`, camelCase web defaults), registered in
  `Drive/DriveServices.cs` with the base address from `Drive:OAuthBaseUrl` / `Drive:ApiBaseUrl` read
  when the client is made, `HttpClient.Timeout` off (each call has 60 s of its own), no redirects,
  the keep-alive connect callback, and `RemoveAllLoggers()` so Microsoft.Extensions.Http never logs
  their requests. Every call returns `HttpResponseMessage`; the bearer is passed per call as
  `[Header("Authorization")]` ("Bearer " + token, as the ElevenLabs key is), so `DriveClient` can
  renew a refused token (401) once and repeat the call. YouTube's is `IYouTubeOEmbedApi` (`YouTubeJson`,
  the record path's titles, `GET /oembed?url=<watch link>&format=json`), registered in
  `YouTube/YouTubeServices.cs` with the base address from `YouTube:OEmbedBaseUrl` read when the client
  is made, `HttpClient.Timeout` off (`OEmbedTitles` gives each read 10 s of its own and starts them at
  least a second apart), a `SocketsHttpHandler` with `UseCookies = false`, `YouTubeHostGuard`, and
  `RemoveAllLoggers()`; `OEmbedTitles.RefitSettings()` lets a failed connection come through as its own
  `HttpRequestException`. It returns `HttpResponseMessage`, so a video YouTube does not answer for is
  no title, never thrown. yt-dlp is a program, not an HTTP API: it runs through `IToolRunner`.
- No `ArgumentNullException` guards on DI-injected parameters.
- Registration: each area registers its services in its own `<Area>Services.cs` (`SettingsServices`,
  `StorageServices`, `AudioServices`, `TranscriptionServices`, `DriveServices`, `YouTube/YouTubeServices`
  (`AddYouTube`, after `AddDrive`), `DesktopServices`, `CommandServices`,
  and last `Commands/WindowServices.cs`, the window's side of a run in the background, which
  replaces the stand-in `NoStartupReport`), called from `ServiceCollectionExtensions.AddClassScribe`;
  `AppHost` adds the settings reload (`SettingsServices.AddSettingsReload`: the places, the file, the
  reloader and the one `SettingsWriter`), since it builds the configuration the reload works on;
  `AddSettings` registers `SavedClasses` and `IOutputFolderFacts` beside `SettingsCheck`, and
  `CommandServices` the settings and class commands, `CommandOutput`, and menus C and S
  (`ClassesMenu`, `SettingsMenu`, per run). `AppHost.Build` validates scopes and every registration
  (`ValidateScopes`, `ValidateOnBuild`), so a per-run service taken outside a run's scope, or one
  that cannot be made, fails at once. `AppHost.BuildRecorder(start, apiKey, log, folder, places,
  time, configureServices)` builds the recorder's host from the same registrations plus `AddRecorder`
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
  `RecordingSession`, `TrackSetFactory`, `MicrophoneAdvice`, `MicrophoneMenu`, `RecordingMonitor`,
  `ElevenLabsAccount`, and for YouTube `YouTubeCommand`, `YouTubeMenu`, `YouTubeBatch`, `DownloadBatch`,
  `VideoBatch`, `DownloadedAudio` and `RecordingUpload`), take `IOptionsSnapshot<T>`, read once in the
  run's scope; YouTube's singletons (`ToolLocator`, `BrowserLocator`, the oEmbed client's
  `ConfigureHttpClient`) read `IOptionsMonitor<YouTubeOptions>.CurrentValue`. Never `IOptions<T>`
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
  and its paid answer laid out, as a class's); `DriveRoom.ReadAsync` (Drive's room only adds a line to
  the plan); and `CommandLineTree.YouTubeProblems` (a `--list` file that cannot be read is a mistake of
  the command line, said with the others).
- Windows audio objects: the device check (`WasapiLoopbackCaptureProvider`'s two listings, once a
  second for the whole class) goes through ClassScribe's own declarations in
  `Audio/Windows/CoreAudioInterop.cs` and releases every COM object it receives in a `finally` block
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
  `DevicesCommand.Read`) catch `AudioFailures.IsFailedResult`. `CoreAudio.Check` takes the checked
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
- The QPC timeline (`Audio/Timeline`, design section 3): a packet carries the performance-counter
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
  `IOptionsSnapshot` and `IOptionsMonitor`; `Set` fires `OnChange`) and `TestSettings` (a real gate
  over a temporary `appsettings.json`). Write hours of audio with `WavSegmentWriter` at 10 Hz, so
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
  message is added or changed. The lines the README quotes for the cost and the Bluetooth warning are
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
  `DriveCommand.Size`'s units (1 MB is 1,048,576 bytes; its `.wav` alone is 722 MB). The YouTube lines
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
  and summary), `VideoWindow.HandedOff`, `BrowserLocator.NotFound`, `OEmbedTitles.NoTitle`,
  `VideoEndWatch` (the rules' lines, `PressPlay`), `VideoEnds`, `RecordCommand` (a video's start and end
  lines), `RecordStarter` (the yield, `Explain`, a lost video recorder), `StopSignal` (the yield's
  lines), `StartupReport` (a video named as the video), `LocalAudioFreer` (a video's audio kept),
  `SettingsCheck` (the YouTube refusals and the `LongestVideoHours` warning), `StayAwake.VideosReason`
  and `TranscriptFormatter.SourceLine`; reword them with those README rows.

## Working with Gulzar

- Balanced answers: not terse, not padded. No em dashes.
- When questions are needed, ask them all at once, as multiple choice where possible.
- Cite sources for research and other non-code answers.
- Arabic terms in IJMES transliteration (this governs `src/ClassScribe/keyterms.txt`).

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
  from `develop`, in a worktree under `%LOCALAPPDATA%\csw` (never `%TEMP%` or OneDrive), committed at the end of every
  session after `git status` has been read for deletions nobody made. Before its merge it is rebased on `develop`, the
  suite run again, and `git range-diff` read, so that a conflict resolved in the rebase is reviewed too. It is merged
  with `--no-ff` when green and reviewed, so `git revert -m 1 <merge>` takes it out again (and reverting that revert
  brings it back). A feature goes into `develop` only when it may reach a class with the next release.
- A release is `release/<version>`, cut from `develop` once its steps are merged, one open at a time. The cut is one
  commit: the `<Version>` line in `src/ClassScribe/ClassScribe.csproj` (and, from step 1, the version of each
  `DefaultChanges` row left empty). Then its weekday dry run through the kit, from the release's own worktree, which
  records the commit it tested. The finish: `main` merged into it first when `main` holds a hotfix it lacks, and the
  suite run again; when its head is not the commit the dry run tested (that merge, or a fix after the dry run), a
  five-minute kit run (`-Short`) on the head first; then in the checkout `git merge --ff-only release/<version>`,
  `git tag -a v<version>`, the push (his OK); the release merged into `develop` with `--no-ff`; the branch and its
  worktree removed; and, until step 2, the release built and installed the day before its first class with
  `Start-Class.ps1 -Name "Mantiq" -NoRecord`, away from any class (from step 2, the installer).
- A hotfix is `hotfix/<version>`, from `main` (or from the head of a release `main` is about to reach), with its own
  weekday dry run (short when the fix is narrow), finished as a release is, then merged into `develop` and into a release
  then open.
- Versions: every release takes a new minor number, a hotfix the next patch number of the release it fixes. Version
  strings are set only on release and hotfix branches, never on a feature branch. 0.3.3 is `43ba9c6` with no version
  line, so its build shows `1.0.0+<commit>`.
- The way back on a class day, until step 2: the previous tag checked out in the checkout
  (`git switch --detach v<previous>`), which `Start-Class.ps1` then builds; `git switch main` once a hotfix is finished.
  Never during a class.
- Dry runs, and every manual run of a side build, go through the kit in `tools\dry-run` (its `README.md`).
  `Start-DryRun.cmd`, double-clicked by Gulzar in Explorer so that the window and its recorder start outside any job,
  publishes the worktree to `%LOCALAPPDATA%\Programs\ClassScribe-Dry` (never through an installer, never single-file),
  starts the stand-in for ElevenLabs (`ElevenLabsStub.cs`, a .NET 10 file-based app on 127.0.0.1), sets a dummy key,
  `ElevenLabs__BaseUrl`, `Drive__Enabled=false` and `CLASSSCRIBE_SETTINGS_FOLDER` in its own process only (never at
  user scope; builds before saved classes and the settings folder (build plan step 1) read only the `appsettings.json`
  beside their exe and pass over the variable, and the kit says which the build reads), runs `record ... --new --output
  "%USERPROFILE%\ClassScribe dry runs"`, checks the run
  file (`home` `Separate`), the transcript, the recording's log and the stand-in's log, compares the listings of
  `%LOCALAPPDATA%\ClassScribe`, `%APPDATA%\ClassScribe` and `%USERPROFILE%\ClassScribe` before and after, and adds the
  result to `%LOCALAPPDATA%\ClassScribe-DryRuns\runs.log`, which a release's finish reads. It refuses while a class
  records, by the run files, never by opening `recording.lock`, and while any run waits for the report at the start
  (`RunView.NeedsReport`, mirrored by the script's `Get-UnreportedRuns`), which the side copy's own report would act on.
  The stand-in is started with `--parent <the script's pid>` and ends with it. The kit's tests write their run files and
  log lines with the app's own writers (`RecorderJson`, `RecorderLogProvider.Text`), so a change to either that the
  script does not follow fails them. `-Real` (billed) and `-Player` (plays a clip aloud
  through the test player) are only at Gulzar's word, which the script asks him to type. Agents stop every ClassScribe
  process they started before a session ends, and leave none running over a class time.
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
dotnet run --project src/ClassScribe                                   # the menu (a recording then runs in the window)
dotnet run --project src/ClassScribe -- record "Kalam" --device auto --output "<folder>"       # record; Q twice stops and transcribes
dotnet run --project src/ClassScribe -- record-only "Kalam" --app chrome --output "<folder>"   # record only
dotnet run --project src/ClassScribe -- record "Kalam" --app chrome --mic --mic-follows chrome --output "<folder>"   # the class and the microphone
dotnet run --project src/ClassScribe -- record "Kalam" --in-person --output "<folder>"         # the microphone alone, in person
dotnet run --project src/ClassScribe -- mic-check --seconds 600        # watch what apps do with the microphone; opens nothing
dotnet run --project src/ClassScribe -- camera-check "OBSBOT"                   # the cameras and the set-up; opens the camera for 5 s, shared, only after a typed Y (the user's word only)
dotnet run --project src/ClassScribe -- transcribe "C:\path\to\folder-or-file"   # --no-open opens nothing; --output for a file
dotnet run --project src/ClassScribe -- transcribe "<path>" --again    # one with a transcript; billed again
dotnet run --project src/ClassScribe -- rebuild "C:\path\to\folder-or-file"      # free, from the saved .json; --no-open
dotnet run --project src/ClassScribe -- finish-video "C:\path\to\lesson"         # a lesson's video made again, free, from its pieces; --no-open
dotnet run --project src/ClassScribe -- list                           # the recordings, newest first; --output
dotnet run --project src/ClassScribe -- devices                        # the devices and apps with sound
dotnet run --project src/ClassScribe -- settings list                  # every setting; set a CLASSSCRIBE_SETTINGS_FOLDER first, so his real settings stay as they are
dotnet run --project src/ClassScribe -- class list                     # the saved classes; class add|set|rename|remove change his file: ask first
dotnet run --project src/ClassScribe -- drive status                   # the Google sign-in; asks Google (ask Gulzar first)
dotnet run --project src/ClassScribe -- drive signin --client "<file>" # a real Google sign-in in the browser: Gulzar's to make
dotnet run --project src/ClassScribe -- drive signout                  # revoke, forget the token, keep the client
dotnet run --project src/ClassScribe -- drive upload "<file or folder>" # a real upload to Google: Gulzar's to make; no path: the queue
dotnet run --project src/ClassScribe -- drive free-space "<file or folder>" --yes   # deletes local .wav once Drive has them, checked
dotnet run --project src/ClassScribe -- youtube check                  # yt-dlp, Deno, FFmpeg, FFprobe: paths, versions, age; local only
dotnet run --project src/ClassScribe -- youtube "https://youtu.be/abcdefghijk" --name "Risala" --output "<folder>" --yes   # records as it plays; Enter before each video; a real Chrome window and YouTube (ask first)
dotnet run --project src/ClassScribe -- youtube --list "<file>" --name "Risala" --output "<folder>" --download --plan     # reads each video with yt-dlp, prints the plan (reaches YouTube: ask first)
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
  (`recorder.old.log` at 1 MB) until the recording's folder is known, then into `<base>.log`. Once
  Google Drive is set up, the recorder lives on after the end has gone to the windows, for the upload
  of its class and of the queue (step 8, below), while the next class may already record. A YouTube
  video recorded as it plays is a recorder's run like a class's (under `dotnet run`, in the window, as
  a class is); its Chrome window is started by the batch's window, never by the hidden recorder.
- `record` and `record-only` need the name and a source (`--device "<part>"`, `--device auto`,
  `--app <process>` or `--in-person ["<microphone>"]`), or `--class "<saved class>"` with options over
  the class's own in groups (`RecordClassAsync`, `ClassChoices`); a saved class's name without a
  source is pointed at `--class` (N5; the validator runs before the gate's read for a recording, so
  `SavedClasses.FindInFile` looks in the file as it is now when the settings in force hold no such
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
  menu asks C, N or Esc (`ContinueQuestion`), and only a recording of the same `RecordingMode` is
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
  `CommandLine.YouTube` prints the record-path command the summaries give. Commands printed for later (`CommandLine`) name the program by its full
  path, keep the microphone options (`--mic`, `--in-person`, `--mic-follows` through
  `FollowRule.Word`) and escape PowerShell's `$`, backtick and double quotes.
- One recording at a time: `%LOCALAPPDATA%\ClassScribe\recording.lock` (`RecordingLock`,
  `FileShare.None`, held only while capturing, so the next class may start during an upload). Run
  files in `runs\<run id>.json` (`RunJournal`, `RunRecord`: state, a heartbeat every 10 s, `EndedBy`,
  the outcome, `Seen`; written atomically, retried three times 100 ms apart; seen runs pruned after
  30 days) are the C5 marker and the next start's report (`StartupReport`: C through
  `ICommandRunner.RecordAsync` with `StartOrigin.StartupReport`, T, O and F carried out by the
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
  stand-in ClassScribe.exe without a window, which records nothing, and runs the whole dry run with
  it in temporary folders, and builds the test player without starting it (about half a minute:
  before merging a change to `tools\dry-run`, and never within an hour of a class, since a process
  named ClassScribe runs for a few seconds).
  `EndToEndTests` also covers in person from the command line, the class with the microphone
  following chrome through a cycle of listening and not (mixed by the real preparer), and
  `mic-check --seconds 1`, a download batch of two videos over `FakeToolRunner`, `StubHttpHandler` and
  `GoogleWorld`, and a batch of two videos recorded as they play through the real `RecorderHost`
  (the window's settings point `YouTube:OEmbedBaseUrl` at the stub; the recorder gets
  `FakeVideoWindows` and a `VideoPlayback` over the test's processes). Nothing is played or recorded. The suite had 887 tests at the end of
  increment 1, 2,134 at the end of increment 2, 2,822 at the end of increment 3, 2,893 after the
  cost line and the Bluetooth warning, with the review's fixes, 3,046 after the Google Drive
  sign-in, 3,111 after the course and date folders, 3,270 after the uploads and free-space,
  3,333 after the uploads by themselves, and 3,357 after the review's fixes to them (a recording
  queued again during its upload, a folder moved or deleted by hand, a done recording asked for
  again, a grown log whose copy is gone or trashed, a manifest that cannot be written, the recordings
  folder with a kept answer at its top, choice 4 on a freed class, the queue counted without a class
  being recorded, the real `%LOCALAPPDATA%\ClassScribe` refused in tests), 3,370 after a false
  start stopped taking its date folder. From there two branches grew apart: the dry-run kit (step 0a)
  reached 3,387, and 3,395 after the review's fixes to it (the refusal while a run waits for its
  report, the prune by heartbeat, the run files and log lines made by the app's own writers, the
  windows left open named, the stand-in tied to the script; its 25 tests take about 10 seconds, and
  its 3 Explicit `DryRunKit` tests about half a minute); YouTube reached 3,670 after its slice 1 (the
  download path), 3,819 after slice 2 (recording as it plays), and 3,875 after the review's repairs
  to them. With both merged into `develop` (step 0) the count is 3,900, all passing, in about
  three minutes; 3,901 with 0.4.0's microphone timing fix, and 3,960 with the Zoom fix (step 0b)
  merged on 22 October. From YouTube's 3,875 the camera's basics grew on `feature/camera-basics` to
  4,001 (camera-first step 11 without the probe's parts: the target framework, the device guard, the
  camera list, its settings, the camera check, the camera's choice and refusal, and the camera
  process's role), 4,012 after the review's fixes to them (a camera job or line without a body, a
  tool start that throws, and the tests that pin the microphone guard's wiring and the camera start's
  order and registration), and 4,068 after the camera's shared open (the probe fix's order), the
  camera process's hold and check jobs, the webcam hold, the webcam zeros rules and the camera
  check's open after a typed Y, and 4,095 with the fixes that keep a camera failure from the
  microphone and pin the camera's rules; merged into `develop` on 22 October, 4,180. From the
  camera's basics, the camera recorded grew on `feature/camera-record` to 4,194 (slice 7: `ClockState`,
  the pieces' files, the record job and the sink writer, the camera track, the run's camera, its soak
  and the end-to-end lesson), and 4,218 after the review's ten fixes to it; merged into `develop` on
  22 October, 4,303. From the camera's basics at 4,012 (`2b64a66`), settings and saved classes grew on
  `feature/settings-classes` to 4,228 (the build shown everywhere, the user's own settings folder, the
  writer and its full copy, `settings` and `class`, `record --class`, `--output` optional, the
  recording that drops refused settings, menus C and S, the folder warnings), and 4,245 at its head
  (the review's fixes and the soak's check by its stamps). Merged into `develop` on 22 October, with
  `Recording:CameraOffsets` made a map of the catalog, the count is 4,538, all passing. From there the lesson's video
  (step 13, slice 8) grew on `feature/lesson-video` to 4,651, all passing, in about four minutes. The uploads by
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
  three minutes (2 min 9 s for 3,357 tests on 17 October 2026, 2 min 16 s for 3,387 on 20 October,
  2 min 17 s for 3,395 later that day, 3 min 55 s for YouTube's 3,875 on 18 October, 3 min 7 s for
  3,900 with both merged on 22 October, 2 min 55 s for 3,960 with the Zoom fix, 3 min 4 s for 4,180
  with the camera's basics, 3 min 34 s for 4,303 with the camera recorded, 3 min 32 s for 4,538 with
  settings and saved classes, plus the build).
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
  in person, lists the cameras (`MicrophoneMenu`'s optional `ICameraProvider`) and asks the size (`Picture size: Enter for
  720p (your setting), 1 480p, 2 720p, 3 1080p, T to type a size`), held by `SourceChoice` (`PickMicrophone(..., camera,
  size)`, `PickedCameraResolution`, `CameraResolution`; W and N drop the camera; the header adds `with the camera "..."
  (720p)`). `RecordStarter` hands a plan with a camera to `CameraStart` (scoped; before the continue question, amendment
  17), which resolves the size into the request (`--resolution`, else menu 7's pick, else `Recording:CameraResolution`,
  else 720p, so the recorder never reads it and the run file keeps it), then refuses with the set-up's first problem or
  `ICameraRecorder.NotReady`: in the menu `Camera "...": <why>. Enter records the sound only; Esc goes back.`, elsewhere
  `<why>. Nothing was recorded; leave out --camera to record the sound alone.` with exit 1; otherwise the menu asks
  `Enter records with the camera; S records the sound only.`, and the command line goes on. `ICameraRecorder`
  (`Camera/CameraRecorder.cs`, with `ICameraTrack`, `CameraTrackStart`, `CameraTrackState`, `CameraConcern` and
  `CameraOffsets`) is registered as `CameraRecorder` (slice 7, "The lesson's picture" below; `NotReady` only before Windows
  10 version 2004); `CameraRecorderNotBuilt` stays as a stand-in for the refusal's tests, a `RecordStarter` without a
  `CameraStart` refuses with its words, and `TrackSetFactory.Create` throws `CameraNotBuiltException` for a plan with a
  camera outside person, or without a ready camera recorder. `CommandLine.Record` keeps `--camera` and `--resolution`. Amendment 2's extension data: `RunRecord`,
  `RecordRequest` and `SourcePlan` carry `[JsonExtensionData] Dictionary<string, JsonElement>? Extra` (null when nothing
  unknown was read, so records still compare equal), with a setter, not `init`: the generated reader fills init-only
  properties through the constructor, where extension data cannot go. A build older than this one still drops what it
  does not know when it writes a run file back. Tests: `CameraMenuTests`, `CameraStartTests`, and additions to
  `CommandLineTreeTests`, `CommandLineTests`, `RecordStarterTests`, `RequestsTests` and `TrackSetTests`.
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
  `LocalTimeZone` (`ClassFolders.DateFolderName`, `yyyy-MM-dd`), `yyyy-MM-dd (2)`, `(3)` for later
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
  then its date folders by name; `IsDateFolderName` is `yyyy-MM-dd` of a real day, optionally
  ` (N)`; other subfolders such as `Slides` are passed over), so `FindNewest`, `RecordingsIn`,
  `FindContinuable`, the list (`ListRecent`, `ListAll`, one warning per unreadable course folder,
  date folders read with `IgnoreInaccessible`), `RebuildCommand.FindInFolder` and `WhereRecorded` (an
  answer kept in the root is laid out beside its recording in either layout) take a course folder, a
  date folder or an old name folder. `ListCommand.NotesOf` shows `in 2026-10-17 (2)` for a later
  class of its day (`ClassFolders.LaterClassOfItsDay`). `ClassFolders.DriveLevelsOf(folder)` is the
  Drive path under `Drive:FolderName` that `RecordingUpload` uses: a date folder gives its course and
  itself, any other folder its own name, whatever the recordings folder (so `--output` folders mirror
  the same way). Files in a recording's folder that `RecordingFiles.TryParse` does not take for the
  recording's (his PDFs, slides and notes, even one named `Mantiq_2026-10-17_Notes.md`, which has no
  time in its stamp) are ignored by every listing, never uploaded, never deleted. Files are named
  `<name>_<yyyy-MM-dd_HH-mm-ss>Z` (UTC start) with `.wav`, `_partN.wav`, `.mic.wav` and
  `_partN.mic.wav` (the microphone's track beside the class, `Track.Microphone`,
  `RecordingFiles.FindSegments(Track)`; in person the microphone is the main track under the plain
  names), `.marks.txt` (marks, and notes written as `hh:mm:ss.fff ~ words`, such as the resume note
  `ClassMarks.AddResume` writes, the sleep notes and the gap notes of class-only recordings;
  `ClassMarks.PartsOf` counts the parts), `.log` (everything the recording's runs logged, in both
  homes, `RecorderLog.WriteFileTo`), `.md` (the Markdown transcript), `.json` (ElevenLabs' answer),
  `.upload.wav` (the 16 kHz copy, the two tracks mixed when there are two, removed after every try,
  failed or cancelled included, unless `Recording:KeepUploadFile`) and `.unread.json` (an answer
  ElevenLabs charged for that did not become a transcript; a `.json` left without its `.md` counts as
  one too, see `RecordingFiles.FindKeptAnswers`; menu choice 3 shows `answer kept` and asks before a
  charged retry), and for a lesson with the camera its pieces, `<base>.cam.mp4` and `<base>.cam@hh-mm-ss.fff.mp4`
  (`RecordingFiles.CameraPiece(start)`, `FindCameraPieces()` in their order on the clock, `IsCameraPiece`; a sign of a
  class for the date folders; held while written for `IsBeingWritten`, the newest piece open for writing with reading
  allowed; never sent to Drive, `RecordingUpload.FilesToUpload` and `UploadManifests.OnDisk` leave them out; a lesson with
  pieces continued only by a run with the camera, `ClassFolders.FindContinuable(..., camera)`; one without pieces is the run
  files' to decide, `ContinueQuestion.FindContinuable`: a run that asked for the camera is continued with it, a piece or
  none, and with no run file left, no piece means no camera). `Track` has no
  `Camera` value: `FindSegments` stays `.wav`'s. `RecordingFiles` owns the naming and `TranscriptCopies` the copy numbers: a
  transcript is never overwritten (`(2).md`, `(3).md`), not even by two processes at once, because
  each copy's `.json` and `.md` are claimed with `FileMode.CreateNew`. `HasTranscript` counts any
  copy; `ClassFolders` measures a recording by its longest track, reports `HasMicrophone` (`list`
  and menu choice 3 say `class + microphone`), and continues a recording only with the tracks it
  was made with. The microphone check's logs go to `<root>\mic-check\mic-check_<local stamp>.log`
  (`MicCheckLog`: no `Z`, so `RecordingFiles.TryParse` never takes one for a recording, and the
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
- Transcription: `ElevenLabsAnswer` is the only reader of ElevenLabs' answers, for the client and for
  rebuild. `UploadTimeLimit` gives each try 10 minutes plus the upload at
  `ElevenLabs:SlowestUploadMbps` plus a quarter of the audio, rounded to the minute, never below
  `ElevenLabs:TimeoutMinutes` (2 h of audio: 71 minutes); `HttpClient.Timeout` is off, as
  `ElevenLabsClient` holds the limit. A recording longer than `TranscriptionWorkflow.LongestRecording`
  (9.5 h) is refused before anything is prepared, measured by the class track alone (the
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
  `UploadTimeLimit.AudioLength`, at `ElevenLabs:PricePerHourUsd` plus `ElevenLabs:KeytermsPricePerHourUsd`
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
  (`DescribeMissing`) and refuses every change; else the shipped `src/ClassScribe/appsettings.json`
  beside the exe, the template, under `dotnet run` the copy in `bin\Debug\net10.0-windows10.0.19041.0`,
  where the menu warns when the project's file is newer). `CLASSSCRIBE_SETTINGS_FOLDER` names another
  user folder, for dry runs, and the menu says so. `SettingsWriter` is the one writer of that file
  (registered in `AddSettingsReload`): every change (menu S and C, S after choices 6 and 7, `settings
  set|reset|open|fill`, `class add|set|rename|remove`) goes through `Change`, under
  `%LOCALAPPDATA%\ClassScribe\settings.lock` (`FileShare.None`, 2 s), from a fresh read, checked by the
  reloader's own staging before it is written (temp file and move, the file before kept as
  `appsettings.previous.json`), then reloaded. The first change writes a full copy of every setting
  (`SettingsCatalog`, by reflection over the options classes in declaration order, plus
  `Logging:LogLevel` as a map), under a three-line `//` header, and the marker; it never writes the
  key (`ApiKeyRefused`; one a hand edit put in the file is taken out of it and of the copy before,
  `ApiKeyTakenOut`, and reset --all keeps no copy of a file that is not JSON and mentions one,
  `ApiKeyNotKept`) and refuses a file with comments of its own (`CommentsOfItsOwn`: any comment after
  the opening brace, and before it any that is not a line of a header in `KnownHeaders`, in order,
  written as a line comment; a later build that rewords the header adds the old one there). Keys a
  newer build wrote are kept; every later change adds the settings the file lacks at their defaults
  and says so with the build's version (`AddedLines`), and `settings fill` does that alone (refused
  with exit 1 while the user's file is missing). A changed default
  needs a row in `DefaultChanges.Rows` (old, new, version), which `settings list` and the menu's header
  show for a file holding the old one (`Recording:MicrophoneFollows`, `chrome` to `auto`, is the
  first). Saved classes are `Classes:<name>:<field>` in the same file (`SavedClasses`, a singleton
  reading `IConfiguration` at each use; `ClassFields.All` in order; unknown fields kept; each class
  checked on its own, so an unusable one never holds up the rest or the file); `ClassChoices` applies
  the command line's or the menu's answers in groups. Menu C's E takes its Enter answers from the
  class, or for an unusable one from its raw fields (`ClassesMenu.Kept.FromFields`), so Enter through
  every question keeps them; a folder typed in menus S and C is resolved by `ClassFolders.RootFor`,
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
  while ClassScribe runs. Warnings are given once per process: a key in the settings, and a
  recordings folder (`Recording:OutputFolder` or a class's `OutputFolder`) inside OneDrive, Google
  Drive for desktop or Dropbox, at a drive's root, the user folder itself, or holding `AppData` or
  `Windows` (`SettingsCheck.FolderWarnings`, over `IOutputFolderFacts`). `SettingsReloader.Snapshot` gives the Recording, ElevenLabs (never the
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
  `ElevenLabs:ReadAccountUsage` (true). The Drive section (`DriveOptions`, `Drive/`): `Drive:FolderName`
  (`ClassScribe`; not empty, at most 100 characters, no control characters; names the root folder
  only when it is first made), `Drive:ApiBaseUrl` and `Drive:OAuthBaseUrl` (Google's; https, or
  http only to a loopback address, for a stub), `Drive:Upload` (`Everything` or `WithoutAudio`, by
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
  place of `Drive:Upload`), `YouTube:LongestVideoHours` (4; from 0.25 to 9; above
  `Recording:StopAfterHours` it is a warning from `FindWarnings`, never a refusal, since a refused file
  would hold up every class over a setting of the videos', and the plan then names the safety stop)
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
  (`IBrowser`, `ShellBrowser`, which never logs the address) are seams. The address carries PKCE
  S256, `drive.file` alone, `prompt=consent` and `access_type=offline`; the wait is 5 minutes on the
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
  `UploadManifest.TimesQueued`, which `UploadManifests.Queue` counts up; `KeepWaiting`, what an upload
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
  folder is `DriveFolders.FindOrCreatePathAsync(ClassFolders.DriveLevelsOf(ClassFolders.AsOnDisk(folder)))`
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
  clears it), so `IsBeingUploaded(now)` (waiting, tried, no error, written within `GoingOnWithin`, 5
  minutes) tells an upload going on now, in any process, and a recording queued again right after
  its upload is not taken for one; `BytesOnDrive` says how much Drive has. `ResumableUpload`: pieces of `Drive:ChunkMegabytes` MiB
  (`PieceSize` for tests), each `PieceContent` with its Content-Range streamed from the open file;
  308 confirms by its `Range`, 200/201 completes; a passing failure (Unreachable or Busy: 408, 429,
  5xx, 403 rate limits) waits as `DriveRetry` says (2, 4 ... 64 s plus up to 1 s, 8 attempts) and
  asks the session `bytes */size`; 404/410 starts a new session once; a kept session is used only
  for the same size, MD5 and target, under six days old, at Drive's own address
  (`UploadAddress.TryParse` against `Drive:ApiBaseUrl`: scheme, host, port, path); a piece may take
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
  file that cannot be deleted stays; a sharing violation is the answer; no upload copy),
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
- Still to be checked with Gulzar against the real Google (design 5.3), never from a test or a manual
  run without asking: the Google Cloud set-up with the app published before the first sign-in; the
  real sign-in, and whether Bitdefender lets the browser reach `http://127.0.0.1:<port>/`; that
  Google's console and consent screens still match the README's steps; then a publish to
  `%LOCALAPPDATA%\Programs\ClassScribe` with a renewed Bitdefender ATD exception. Only `FakeGoogle`
  (the generated Refit client, the real app's DI clients in `DriveSecretsTests`) and the design's local
  probe have exercised the 308/`Range` handling, the `Location` format, and Refit's `[Body]
  HttpContent` with `Content-Range` through a real `SocketsHttpHandler`; the 403 reasons
  `uploadLimitExceeded` and `dailyLimitExceeded` were not checked against Google's error pages. The
  first real class with Drive set up is the first time the hidden recorder lives on after the window
  has the end and the first time it reaches the network (Bitdefender may notice). The first real
  upload is Gulzar's to start: his class of 17 October (about 727 MB, once he has moved it to
  `%USERPROFILE%\ClassScribe\Mantiq\2026-10-17` and renamed its files `Mantiq_2026-10-17_14-04-42Z.*`;
  his PDFs and notes beside it stay on the laptop). Its `.json` was re-indented by his editor (tabs,
  CRLF, 5.2 MB) and still parses whole (39,543 words, 7,884 s), so `ElevenLabsAnswer` and `rebuild`
  must keep reading pretty-printed answers. His run file `20261017-140441Z-af758de8.json` in
  `%LOCALAPPDATA%\ClassScribe\runs` still names the old `Mantiq_10_17_2026` folder; it looks finished,
  but if the start-up report ever names it, that is why.
- YouTube, what both paths share (design `youtube.md` section 2; `src/ClassScribe/YouTube`): `youtube`
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
  `is_upcoming`, `post_live`; no length; over 9.5 h; `ChannelUnknown` while `CourseChannels` names any
  and the read names no channel; `MayDownloadChannels`; public; else a word, or `ready (--allow-unlisted)`
  for one `AllowUnlistedTakes`: unlisted, of a known channel), the plan (`DownloadPlan.Lines`,
  `ReadyLines` with `Waiting for your word`, `FetchLines`; `PlanRoom` with `ClassReserve`, room for a
  class of `KeptForAClass` = `RecordingMonitor.SpaceWarningBelow` (3 h) by `RecordingMonitor.RoomFor`,
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
  `Recording:StopAfterHours`, named), Y in the menu (which opens the first at once), then per video:
  `ClassWait`; the `Next: ... Enter opens it; S stops ...` prompt (Enter, S or Esc, Ctrl+C twice, never
  a window without his key; `--yes` asks it before the first too); `ClassWait` again after Enter;
  `VideoWindow.OpenAsync` (`IProcessStarter.StartBrowser` with `VideoWindow.Arguments`:
  `--user-data-dir=<RuntimeFolder>\video-browser --no-first-run --no-default-browser-check
  --app=<link at its start>`, never an autoplay switch, working folder `<RuntimeFolder>\youtube`; the
  hand-off check: the started process must not have exited within 3 s, or Chrome handed the video to a
  window left open, `HandedOff`, and Enter tries again); `RecordAsync` (a `RecordRequest` with
  `VideoRequest`, `RecordingMode.Class`, `ClassSource.App("chrome")`, `Transcribe` false, `Continue`
  `New`, through `ICommandRunner.RecordAsync` with `StartOrigin.VideoBatch`, shown here as the run's
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
  s; 5 `YouTube:LongestVideoHours` of recording or running, `VideoLimit`), from `RecordingStatus.HeardSound`
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
  gives this program's command line to it and to `ProcessStarter`. `ICameraProcess` gives the channel, the process id, the
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
  does not start, is started again `RestartAfter` (2 s) later, with the software encoder after an `Encoder` problem; the
  `MostFailures`th (3) within `FailuresWithin` (10 min) gives the picture up (`GivenUpText`). Its `State`
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
  `InputStuck` (the starter's writes hang, cancellation passed over, until the process ends) and `DisposeHangs`. Tests:
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
  deletes a stale partial with a line, leaves an unreadable piece out with a warning, logs the plan, sends `progress`
  at each new percent or every `ProgressEvery` (10 s, at most 98 while writing, 99 while checking), and on a pass renames
  the partial (`PutInPlaceAsync`: `RenameTries` 3, a second apart, while a player or scanner holds a file open; then failed
  with why, the checked partial kept for the next making to delete, the older video and the pieces as they were) and deals
  with the pieces: kept when the job's `KeepPieces` says the video goes to Drive (a line), else
  deleted with a line; a failure deletes the partial and keeps the pieces, with the free `finish-video` command; a cancel
  is 130. In the window or recorder, `LessonVideoMaker` (transient, `CameraServices`) starts the camera process with a
  finish job and gives a `LessonVideoResult` (`Made`, `Failed`, `Cancelled`; ended after `ProgressLimit` 2 min without a
  line, `CancelWait` 15 s, `SendLimit` 5 s, `DisposeWait` 5 s) and says the end (`MadeLine`: watch it before publishing).
  `RecordCommand` sets the run file to `Transcribing` at a camera run's stop (the state now covers the video's making),
  starts the video beside the transcription (`StartTheLessonVideo`; no pieces is a warning) with
  `LessonVideoDrive.KeepsPieces`, and waits for it on every end; the first lesson's start line says how to turn on
  `Drive:LessonVideos` (`LessonVideoDrive.StartLine`). `finish-video <path> [--no-open]` (`FinishVideoCommand`,
  `ICommandRunner.FinishVideoAsync`) makes it again, free, decided from the files alone (`ClassFolders.LessonVideoStateOf`:
  `None`, `Made`, `NotMade` when a piece is newer than the video or there is none), refusing a lesson being recorded or
  made, one without pieces, and one whose sound is on Drive only (`SoundOnDriveOnly`), with keep-awake
  (`StayAwake.LessonVideoReason`), then Drive's upload of that recording. The start-up report offers V after an outcome
  whose video was not made (`LessonRuns`, `StartupAction.MakeVideo`, T only when the recording can be transcribed); menu
  choice 3 notes `lesson video` or `lesson video not made yet` (as `list` does) and takes V before a number. Drive:
  `Drive:LessonVideos` (false) sends `<base>.mp4` last, streamed as `video/mp4` with a line of its size and time, never
  the pieces or the partial (`UploadManifest.OnDisk(files, withLessonVideo)`); the pass also waits while a class is
  transcribed (N13); `LocalAudioFreer.FreePiecesAsync` deletes the pieces once the video is verified on Drive, only those
  not newer than it (a newer one stays, named), and `FreeAsync` refuses a lesson whose video is `NotMade` while its sound is
  here, deleting nothing (`LocalAudioFreer.VideoStillToMake`, with the `finish-video` command). There is no
  `folders.lock` or `MoveJournal` on develop, so `finish-video` and V check neither. Fakes: `FakeLessonVideoMedia`
  (`TestDoubles.LessonVideo.cs`: `Holds`, `Unreadable`, `HoldWrite`, `WriteFailure`, `CheckAnswer`, `Written`, `Checks`,
  the sound's samples). Tests: `LessonVideoPlanTests`, `LessonVideoFilesTests`, `CameraFinishJobTests` (the real host over
  the fake media on the manual clock), `LessonVideoMakerTests`, `LessonVideoMediaTests` (WindowsMedia: pieces from the
  real piece writer, a size change encoded again, a continued lesson, IDR-only sync samples, the check's failures),
  `LessonVideoDriveTests`, `RecordCommandTests.LessonVideo.cs`, `FinishVideoCommandTests`, `FinishVideoCommandLineTests`,
  `StartupReportTests.LessonVideo.cs`, and the lesson's EndToEnd run.
- The webcam hold (`Camera/WebcamHold.cs`; camera-first 3.4 as amended on 21 October, behind
  `Recording:WakeCameraMicrophone`, off by default): `TrackSetFactory` gives each microphone track a `WebcamHold` from
  `WebcamHolds` (registered in `CameraServices`; optional, so a factory without it has no webcam). At each pick
  (`TrackSet.OnMicrophonePicked`) `Follow` finds once per microphone whether it belongs to a webcam
  (`Cameras.WebcamOf`: `CameraOf` but never a virtual camera; the cameras are listed only when `Cameras.MayBeOfACamera`,
  a container that is not the computer's own, on the pool and waited for at most `ListLimit` (5 s; while a listing hangs a
  later pick begins none and waits for none); a list that has a problem, throws or does not come leaves the pick
  undecided, said once per stretch, a warning only with the setting on, and is asked again at the next pick; `Follow`
  never throws, since it runs inside the microphone's pick). With the setting on it starts a
  camera process with a hold job carrying the camera's id and name alone, and `MicrophoneProviderTap`'s `beforeOpen`
  (`WebcamHold.BeforeOpen`) holds the microphone's capture until the first frame, at most `FirstFrameWait` (5 s) after the
  hold began; the track is `ClockAligned`, so the wait is padded. The hold lasts through P and V off, follows the pick to
  another webcam or lets go for a microphone of none (the old process let go in the background), and `TrackSet.Stop` lets
  it go after the tracks have stopped, before the upload (`stop`, at most `StopWait` 5 s, then killed and disposed); leaving
  the microphone out stops it too. A process that does not start or take its job (for any cause) or ends by itself is
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
  the setting on and off, the microphone still opening; a start that fails for any cause; the late start for an earlier
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
  `RecordingSession.OnlyZerosAfter`. Tests in `WebcamZerosTests.cs` (partial `RecordingSessionTests` for the measure, each
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

- `src/ClassScribe`: `Program.cs` (the console set to UTF-16, a start-up failure printed plainly,
  the close prompt), `AppHost` (builds the host: the checked settings source and a first checked
  read, the key from the environment, the plain log format, scopes and registrations validated),
  `App` (the command line, with the gate before each command), `ServiceCollectionExtensions` (calls
  each area's `<Area>Services.cs`), `Durations`, `PlainConsoleFormatter`.
- `src/ClassScribe/Settings`: the options classes (`YouTubeOptions` among them, with `IsChannel` and
  `Expanded`), `ApiKeyFromEnvironment`, `SettingsCheck` (types, enums, ranges and paths, lists, the
  Logging section, the warnings, keyed problems for the recording's drop), `OutputFolderFacts` (the
  folders' facts the warnings use), the places (`SettingsPlaces`: the user's folder, the program's,
  the marker, the Missing state), the reload (`SettingsFile`, `CheckedSettingsSource`,
  `SettingsReloader`, `StagedSettings`), the writer (`SettingsWriter`, `SettingsDocument`,
  `SettingsCatalog`, `DefaultChanges`), the saved classes (`SavedClasses`, `ClassChoices`) and
  `SettingsServices`.
- `src/ClassScribe/Desktop`: keeping the laptop awake (`IPowerRequests`, `IPowerSource` and
  `StayAwake`, all in `IPowerRequests.cs`), the computer's clocks (`ISystemTimes`), the window's
  title, taskbar and flash (`IWindowSignals`, `TitleText`, `WindowStatusSink`), starting processes
  (`IProcessStarter`), the job check (`IJobInspector`), Windows' end of the session
  (`ISessionEvents`), the registry read only (`IRegistryValues`, HKEY_CURRENT_USER, for the
  microphone advice), the program behind a process id (`IProcessNames`, a limited query) and
  ClassScribe's own processes (`IOwnProcesses`, by pid and by `OwnProcesses.ProgramName`); for YouTube,
  `IRegistryValues.ReadLocalMachineString` (the machine's PATH, Chrome's App Paths), `IFileProbe` (a
  file there, a program's version), `ChildEnvironment.WithoutTheKey`, `IProcessStarter.StartBrowser`,
  `IVideoWindows` (a process's Chrome windows: titles and `WM_CLOSE`) and `IProcessFamily`
  (`ProcessFamily`, a process's tree by pid and start time over `IProcessTree`);
  `Desktop/Windows` holds the Windows calls, `MicrophoneUsageRecord` (the ConsentStore key: which
  apps used the microphone and when) among them, `ChromeWindows` (`EnumWindows` by process, class
  `Chrome_WidgetWin_1`, `GetWindowTextW`, `WM_CLOSE`; refused in tests) and `ToolProcesses.cs` (the
  tool and browser start on `NativeProcessCreationApi`: environment block, handle list, suspended
  start into `ToolJob`; any program but `cmd.exe` refused in tests). `DesktopServices` registers them,
  the usage record, the process names, the own processes, `IProcessFamily`, `IVideoWindows` and
  `FreezeWatch`.
- `src/ClassScribe/Recorder`: the separate recorder: `RecorderHost.cs` (`RecorderStart` and
  `RecorderHost`: the recorder process from the request to the end, with `IsRecorderStart` for
  `Program`), `RuntimeFolder` (`%LOCALAPPDATA%\ClassScribe`), `RecordingLock`, `RunJournal` with
  `RunView` and `RunHandle` (run and request files, `MarkSeen`, `MarkSeenIfOver`, prune),
  `RunRecord.cs` (run file, request file, `RunEndCause`, the video causes among them), `RecorderJson`,
  `ProcessLiveness`, `StopSignal` (with the yield event: `YieldPrefix`, `ListenForYield`, `TrySetYield`,
  `YieldNameFor`, and `IVideoYield` with `WindowsVideoYield`), `RecorderLog.cs` (`LogEntry` and `RecorderLog`: the ring of 20,000 lines, `Written`,
  the files) and `RecorderLogProvider`, `LinkConsoleUi` (the windows' keys as the recorder's
  console), `LinkStatusSink` (states to the windows and the alerts, repeated once a second),
  `RecorderResultShower`, `UnattendedAlerts` (the minimized window brought back by itself; a window
  that left with Esc counts as no close) and `FreezeWatch`.
- `src/ClassScribe/Link`: one named pipe per run (`LinkProtocol`, `LinkTypes` and `LinkKeys` in
  `LinkMessages.cs`, `LineChannel`, `RecorderServer`, `RecorderClient`); messages are lines of JSON
  (`LinkJson`), and a field added to a record that travels on it needs a default.
- `src/ClassScribe/Audio`: capture (`Windows/` holds the WASAPI code), `RecordingSession` (which
  also declares `TrackSpec` and `TrackPlacement`, places `ClockAligned` tracks through
  `Audio/Timeline`, holds packets for a gate, pads to the clock on its own one-second timer, and
  reports `Off`, `OnlyZeros` and `Corrections`), WAV writing (each packet goes to Windows at once;
  the header is rewritten and flushed to disk every `Recording:FlushSeconds`), upload preparation
  (`AudioPreparer`, `UploadTracks`: the microphone's track mixed in); `RecordingMode`, `Track`,
  `IRecording` (the recording a run drives, and its factory), `ICaptureSource` and `LoopbackSource`
  (what the computer plays, through `CaptureChooser`, which follows the run's `ClassSource`),
  `ClassRecording` (an `IRecording` over one session of the class alone), `TrackSet.cs` (`TrackSet`:
  the class and the microphone on one clock, or the microphone in person; owns the gate and the
  watch; `MicrophoneTrack`, and `MicrophoneProviderTap`, the provider as a recording sees it, noting
  each capture opened), `TrackSetFactory.cs` (the registered `IRecordingFactory`: the recording per
  mode, and which app is followed), `SourceChoice` (the picks of menu choices 6 and 7, or the
  settings, as a `SourcePlan`). `AudioServices.cs` holds `LazyLoopbackCaptureProvider`:
  `ILoopbackCaptureProvider` is made on first use, so the app, `transcribe`, `rebuild` and `list`
  never touch Windows audio, and `Lazy<ILoopbackCaptureProvider>` (for `devices`) resolves the
  registered provider.
- `src/ClassScribe/Audio/Timeline`: the QPC timeline of design section 3: `MediaTime` (positions in
  100-ns units, conversions without overflow), `RecordingClock` (origin, offset, pauses and cuts,
  positions, the paused and cut totals, `Snapshot()`), `ClockState` (the clock's whole state, immutable, placing by the
  clock's own code; `ClockExclusion`) and `TrackPlacer` (where a packet goes on a track, the
  padding target, the drift corrections).
- `src/ClassScribe/Audio/Microphone` (namespace `ClassScribe.Audio.Microphones`, plural on purpose: a
  `Microphone` namespace would hide the `Microphone` record): `Microphone.cs` (the contracts:
  `Microphone`, `IMicrophoneProvider`, `IMicrophoneCaptureFactory`, `MicrophoneProcessing`,
  `ClassAppMute`, `FollowRule` with `TryParse`, `Word` and `Named.Matches`, `MicrophoneGateState`),
  `MicrophoneChooser.cs` (which microphone, pinned by id), `MicrophoneAdvice.cs` (the start lines,
  the echo check, the Vantage poll; `MicrophoneStart`), `MicrophoneSource.cs` (the microphone as a
  capture source: chooser plus provider, `Picked`, `FollowApp`), `MicrophoneWatch.cs`
  (`ClassMicrophoneWatch`, `WatchLook`, and the readers' contracts `IMicrophoneSessions`,
  `MicrophoneListings`, `MicrophoneSessionListing`, `CaptureSession`, `IMicrophoneUsageRecord`,
  `UsageRecordListing`, `UsageEntry`), `MicrophoneGate.cs` (`MicrophoneGate`, `IPacketGate`,
  `GateVerdict`, `GateSummary`), `MicrophoneChangeTracker.cs` (the check's lines, `MicCheckLine`)
  `BluetoothMicrophoneWatch.cs` (the class app on a Bluetooth microphone, registered scoped in
  `AudioServices` beside the advice) and `MicrophoneServices.cs` (`AddMicrophone`, called from
  `AddClassScribe`: the capture factory `MicrophoneActivation`, the provider behind
  `LazyMicrophoneProvider`, the sessions reader, and the `Lazy<>`s for `devices`).
- `src/ClassScribe/Camera` (namespace `ClassScribe.Camera`; teach-video section 6 and camera-first step 11, built to the
  part that needs no probe): `CameraDevice.cs` (`CameraDevice`, `CameraPlace`, `CameraPanel`, `VideoDeviceFacts`,
  `CameraList`, `ICameraProvider`, and `Cameras`: the list's rules, colour cameras only, so Windows Hello's infrared camera
  never shows, built in by the enclosure panel first since a laptop's own camera sits on an internal USB bus too, then USB
  or virtual by the id; `Choose`, a name part first, virtual ones included, else USB, else the built-in front camera,
  never a virtual one unnamed; `CameraOf` and `MicrophoneOf`, a webcam's microphone by the container it shares with its
  camera, never a built-in camera and never by the computer's own container `Cameras.ThisComputer`; `WebcamOf`, never a
  virtual camera either, for the hold and the zeros rules), `CameraRoute.cs` (the shared open and its call log),
  `CameraThread.cs`, `CameraJobs.cs` (the hold and check jobs, `CameraProblems`), `CameraRecordJob.cs` (the record job),
  `CameraFinishJob.cs` (the finish job: the lesson's video), `LessonVideo.cs` (`VideoSourceFacts`, `LessonVideoPlan`,
  `LessonVideoCheck`, `ILessonVideoMedia`), `LessonVideoMaker.cs` (the starter's side of the finish job),
  `CameraPieces.cs` (`PieceSettings`, `IPieceWriter`, `IPieceWriterFactory`, `ICameraFrame`, `CameraPicture`,
  `FramePlacer`, `FrameQueue`), `CameraRecorder.cs` (`ICameraRecorder`, `CameraRecorder`, `ICameraTrack`,
  `CameraTrackStart`, `CameraTrackState`, `CameraConcern`, `CameraOffsets`, `CameraRecorderNotBuilt`), `CameraTrack.cs`
  (the recorder's side of a lesson's picture), `WebcamHold.cs` (`WebcamHold`,
  `WebcamHolds`, `WebcamState`), `Windows/WindowsCameraCapture.cs` (the real MediaCapture),
  `Windows/WindowsCameraFrame.cs` (a frame's pixels copied out, `MemoryBufferBytes`), `Windows/MediaFoundationPieceWriter.cs`
  (the piece writer and its factory, `VideoGuids`, `FileStreamAsIStream`),
  `Windows/MediaFoundationLessonVideo.cs` (the lesson video's media work, `H264Pictures`, `LessonVideoGuids`),
  `Windows/WindowsCameraProvider.cs` (WinRT's `DeviceInformation.FindAllAsync` with `System.Devices.ContainerId` and
  `MediaFrameSourceGroup.FindAllAsync`, which open nothing; Windows older than 10.0.19041 and a list Windows will not give,
  whatever exception WinRT maps its HRESULT to, answer with a `CameraList` that says why, and only the caller's own cancel
  is thrown; an internal constructor takes a stand-in read, `WindowsCameraProviderFailureTests`) and `CameraServices`
  (`AddCamera`, after `AddMicrophone`). The microphone's
  container is read by `EndpointFacts` (`PropertyKeys.ContainerId`, PKEY_Device_ContainerId) and carried as
  `Microphone.ContainerId`, a last field with a default. `devices` lists the cameras after the microphones. The fakes are
  `LaptopCameras` and `FakeCameraProvider` (`TestDoubles.Camera.cs`); the real list is the category `WindowsCamera`
  (`WindowsCameraProviderTests`, run with the suite, listing only, no light).
- `src/ClassScribe/Audio/Windows`: `CoreAudioInterop.cs` holds ClassScribe's own Core Audio
  declarations (the property store and its readers, `PropertyKeys`, `EndpointFormFactor`, the
  endpoint volume and session interfaces) and `CoreAudio` (`CreateEnumerator`, `OpenPropertyStore`,
  `Check`, `Release`): the device check and the microphone listing go through them, and NAudio does
  the notifications, the output device names and the captures. `WasapiAudioCapture.cs` is the one
  capture class for devices, apps and microphones (`ForOutputDevice`, `ForApp`, `ForMicrophone`;
  the capture thread is "ClassScribe capture" or "ClassScribe microphone"; packets through an
  internal `IPacketSource` seam, stamped with their QPC position); `AudioClient2Interop.cs`
  (ClassScribe's `IAudioClient2`) and `MicrophoneActivation.cs` (the microphone capture factory and
  Windows' activator, RAW with the fallback); `WasapiMicrophoneProvider.cs` (the microphone listing,
  notifications, mute) and `EndpointFacts.cs` (what a capture endpoint's property store says: the
  array, Bluetooth and loopback rules); `WasapiMicrophoneSessions.cs` (the sessions and mute per
  capture endpoint, with `IAudioSessionControl2WholeRaw`); `WasapiLoopbackCaptureProvider.cs`,
  `ProcessLoopback.cs` and `CaptureGapWatch.cs` as before.
- `src/ClassScribe/Transcription`: ElevenLabs client (a Refit interface plus `ElevenLabsClient`
  around it), the shared answer parser (`ElevenLabsAnswer`), the upload's time limit
  (`UploadTimeLimit`), glossary loader, transcript formatter, workflow (rebuild included),
  `TranscriptionProgress` (scoped: the workflow and the client report preparing, uploading at a
  percent and waiting; `RecordCommand` shows it as UPLOADING 40% and writes it into the run file's
  heartbeat, which the menu header and `list` read), and what a transcript cost: `TranscriptionCost`
  (the estimate), `ElevenLabsAccount.cs` (`IElevenLabsAccount`, `AccountReading`, the reader of
  `GET /v1/user/subscription`) and `UsageReport` (scoped: the read before the upload and the line after;
  `Describe` for a YouTube plan, `Charged` and `UsageCharge` for its projection), and `MarkdownText`
  (a video's title escaped for the transcript).
- `src/ClassScribe/Drive`: Google Drive (design `drive.md`, slices 1 to 3): `DriveOptions` (with `DriveUploadScope`),
  `IGoogleOAuthApi.cs` and `IDriveApi.cs` (the Refit interfaces, `DriveApiSettings`), `GoogleJson.cs`
  (`GoogleOAuthJson`, `DriveJson`, `DriveStoreJson` and their records, which print no token),
  `GoogleDriveException.cs` (`DriveProblem`), `GoogleClientFile.cs` (`GoogleClient`, the import),
  `Pkce`, `ILoopbackReceiver.cs` and `Windows/HttpListenerLoopbackReceiver.cs`, `IBrowser.cs`
  (`ShellBrowser`), `GoogleCredentialStore.cs` (`GoogleCredentials`, `StoredCredentials`,
  `IGoogleCredentialStore`, `DpapiGoogleCredentialStore`), `GoogleTokens.cs` (`GoogleTokenCache`,
  `GoogleTokens`), `DriveClient` (the calls, the 401 renewal, Google's refusals in words and kinds,
  `PieceAnswer`), `DriveFolders` (the root folder and the levels under it, `DriveFolderPath`),
  `GoogleSignIn` (`SignedIn`, `SignOutOutcome`), and the uploads: `DriveRetry`, `PieceContent`,
  `ResumableUpload.cs` (`UploadAddress`, `UploadSource`, `UploadTarget`, `IUploadWatch`,
  `ResumableUpload`), `UploadManifest.cs` (`UploadState`, `UploadSession`, `ManifestFile`,
  `UploadManifest`, `UploadJson`, `UploadManifests`), `DriveUploadLock`, `RecordingUpload`
  (`RecordingOutcome`, `RecordingUploadResult`), `DriveUploadPass` (`PassResult`),
  `LocalAudioFreer` (`FreedAudio`), `LessonVideoDrive` (whether a lesson's video goes to Drive, the start line),
  `DriveCopies` (the `IAudioOnDrive` of `Storage`),
  `DriveAutoUpload.cs` (`IDriveAutoUpload`, `DriveAutoUpload`: the queue at a run's start and the
  upload after it, and after a transcript made later), `DriveQueueReport.cs` (`DriveWaiting`,
  `DriveQueueReport`: the menu's header line and the report's), `DriveRoom.cs` (`IDriveRoom`,
  `DriveRoom`, `DriveSpace`: Drive's room from the about call, for a YouTube plan; null when not set up,
  signed out, `Drive:Enabled` off or Drive out of reach), and `DriveServices` (`AddDrive`, called from
  `AddClassScribe` after the transcription).
- `src/ClassScribe/YouTube` (registered by `YouTubeServices.AddYouTube`): shared parts `YouTubeLink.cs`
  (`YouTubeLink`, `YouTubeLinkKind`, `LinkReading`, `YouTubeLinks`), `LinkList.cs` (`LinkWord`,
  `LinkListReading`, `LinkList`), `VideoSource.cs` (`VideoPlaylist`, `VideoSource`, `YouTubeJson`,
  `VideoSourceFile`), `YouTubeFolders`, `YouTubeGuards`, `YouTubeLock`, `YouTubeRequest.cs`
  (`YouTubeRequest`, `BatchConfirmation`), `YouTubeBatch.cs` (`YouTubeBatch`, `BatchKeys`),
  `BatchPace.cs` (`IBatchPace`, `BatchPace`, `ClassWait`), `VideoIndex.cs` (`PastVideo`, `PastKind`,
  `PastVerdict`, `VideoIndex`), `VideoTranscripts.cs` (`VideoToTranscribe`, `IVideoTranscriber`,
  `ScopedVideoTranscriber`, `VideoTranscriptRun`, `TranscriptOutcome`, `BatchTranscripts`) and
  `TranscriptionRuns.cs` (`TranscriptionRuns`, `TranscriptionRun`); the download path `ToolLocator.cs`
  (`ToolPaths`, `ToolSearch`, `ToolLocator`), `IToolRunner.cs` (`ToolCall`, `ToolLine`, `ToolResult`,
  `IToolRunner`, `WindowsCommandLine`), `ProcessToolRunner`, `YtDlpArguments`, `YtDlpRead.cs`
  (`YtDlpInfo`, `YtDlpPlaylist`, `YtDlpRead` and their JSON context), `YtDlpErrors.cs`
  (`DownloadFailureKind`, `DownloadFailure`, `YtDlpErrors`), `YtDlpCheck.cs` (`ToolCheck`, `YtDlpCheck`),
  `DownloadGuard.cs` (`GuardVerdictKind`, `GuardVerdict`, `DownloadGuard`), `DownloadPlan.cs`
  (`BatchVideo`, `PlanRoom`, `DownloadPlan`), `DownloadedAudio.cs` (`IncompleteAudioException`,
  `ConversionNote`, `DownloadedAudio`) and `DownloadBatch`; the record path `BrowserLocator`,
  `OEmbedTitles.cs` (`IYouTubeOEmbedApi`, `OEmbedAnswer`, `VideoTitle`, `OEmbedTitles`,
  `YouTubeHostGuard`), `VideoWindow.cs` (`OpenedWindow`, `WindowOpening`, `VideoTitles`,
  `VideoWindow`), `VideoPlayback`, `VideoEnds.cs` (`VideoEnds`, `VideoWindowClosedException`) and
  `VideoBatch.cs` (`RecordedVideo`, `VideoPlan`, `VideoBatch`).
- `src/ClassScribe/Storage`: class folders under the output folder (`ClassFolders`: course and date folders, the old flat layout read too; `CreateRecordingIn` and `IsVideo` for YouTube), recording file names (both
  tracks, and a lesson's camera pieces, `CameraPieceFile`), numbered transcript copies (`TranscriptCopies`, never overwritten), marks, the
  microphone check's log file (`MicCheckLog`: the header, the cost line, flushed per line), and
  `RoamingFolder` (`%APPDATA%\ClassScribe`, where `google-drive.dat` is kept; tests give a temporary one),
  and `IAudioOnDrive.cs` (`AudioOnDrive`, `IAudioOnDrive`, `NoAudioOnDrive`: what the lists and commands
  know of audio kept on Drive alone; `ClassFolders` takes it as an optional last argument).
- `src/ClassScribe/Commands`: the menu, the command line (`CommandLineTree` on System.CommandLine,
  with `CommandLineTree.Settings.cs` and `CommandLineTree.Classes.cs`; `App`, `CommandLine` for the
  commands printed for later), `settings` (`SettingsCommand`, `SettingsRequest`, `CommandOutput`,
  `SettingRow`) and `class` (`ClassCommand`, `ClassRequest`), menus C (`ClassesMenu`) and S
  (`SettingsMenu`), the contracts (`Requests.cs`:
  `RecordRequest`, `SourcePlan`, `ClassSource`, `ContinueChoice`, `StartOrigin`, `RunIds`;
  `StarterContracts.cs`: `ViewMode`, `ViewEnd`, `IRunViewer`, `IRecordStarter`, `IRecordRun`,
  `IStartupReport`, `IResultShower`; `RecorderState.cs`), the `record` run (`RecordCommand` as the
  `IRecordRun` in either home, `RecordingLoop` for the keys, states and heartbeat, `RecordingMonitor`
  for the once-a-second checks, `RunBookkeeping.cs` for the lock, the run file, the recording's log,
  keep-awake and the end handlers, with `RunContext`, `RunContextSlot` and `ICrashWarnings`,
  `WindowResultShower`), the starter (`RecordStarter`, `RecorderLauncher`, `ContinueQuestion`), the
  view of a background run (`RecorderView`, `AttachCommand`), the report at the start
  (`StartupReport`, with `LessonRuns` for a lesson's video), `transcribe`, `rebuild`, `finish-video`
  (`FinishVideoCommand`), `list` and `devices` (output devices, apps with sound,
  then the microphones with which apps hold them, from `Lazy<IMicrophoneProvider>` and
  `Lazy<IMicrophoneSessions>`), `MicrophoneMenu.cs` (menu choice 7: W, I or N, the microphone, the
  follow question), `MicCheckCommand.cs` (the microphone check's loop), `YouTubeCommand.cs`
  (`YouTubeCommand`, `YouTubeMenu` for menu choice Y, `YouTubeMenuMemory`), `VideoEndWatch.cs`
  (internal: how a video's recording ends by itself, for `RecordingMonitor`), Google Drive's commands
  (`DriveMenu`, menu choice 9; `DriveCommand`, `drive signin`, `signout` and `status`;
  `DriveUploadCommand`, `drive upload` and `drive free-space`, and what a path names), the gate
  (`RunGate`), the console and desktop abstractions, the name prompt, the close prompt and the exit
  warnings, and `WindowServices.cs` (the registration of the window's side). `RecordingMonitor.cs` also declares
  `IMicrophoneRecording` (what a recording with the microphone tells the run beyond its snapshot),
  `MicrophoneWords` and `MicrophoneChecks` (the microphone's checks beside the class's);
  `ICommandRunner.cs` declares `MicCheckRequest` and the `IMicCheckRun` seam. Each command runs in
  its own DI scope (`ICommandRunner`: record through `IRecordStarter`, attach through
  `AttachCommand`, list, devices, the microphone menu and the check through their commands;
  `TranscribeAsync` refuses a recording a live run records or transcribes). A start from the window
  the recorder opened by itself is `StartOrigin.AutoContinueFromAlert`: shown with the keys locked,
  never recorded in that window, never marked seen.
- `src/ClassScribe/Desktop/Windows`: `ProcessStarter` (`CreateProcessW` behind `IProcessCreationApi`),
  `JobInspector` (`IsProcessInJob` and `QueryInformationJobObject` behind `IJobApi`),
  `SessionWindow` (with `IShutdownReasonApi`), `ConsoleWindowSignals` (`IConsoleWindowApi`:
  `WindowsTerminalVariableSet`, `ClassOf`, `HasTaskbarButton`), `TaskbarList` (`ITaskbarList3`
  declared with `ITaskbarList`'s and `ITaskbarList2`'s methods first, since COM interop ignores
  interface inheritance), `WindowFinder` (`NativeProcessTree`), `WindowsPowerRequests` and
  `WindowsSystemTimes`.
- `tools/dry-run`: the dry-run kit (its `README.md`): `Start-DryRun.cmd` and `Start-DryRun.ps1` (dot-sourced, it
  defines its functions and runs nothing; `Invoke-DryRun -Roots` takes the folders, `Get-DryRunRoots` gives this
  user's), `ElevenLabsStub.cs` (the stand-in for ElevenLabs: `SoundFinder`, `StandInTranscript`, `StandInUpload`,
  `StandInServer`, `StandInProgram`, in a block namespace `ClassScribe.DryRun`), `TestPlayer\` (plays a clip aloud as the
  class, only with `-Player`; not in the solution) and a `.gitattributes` that keeps `.cmd` and `.ps1` in CRLF.
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
  (`TestLoggerFactory`), `TestDoubles.Account.cs` (`FakeElevenLabsAccount`) and `TestDoubles.DryRun.cs`
  (`WindowsPowerShell`, a hidden Windows PowerShell 5.1 for the kit's tests). The cost line's
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
  `ServiceRegistrationTests` and `EndToEndTests`. `Denied.cs` holds ACL helpers; `ClassScribe.Tests.runsettings` holds the hang
  guard. The test project sees ClassScribe's internals (`InternalsVisibleTo` in `CoreAudioInterop.cs`).
