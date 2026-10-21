# The dry-run kit

A dry run tests a release or hotfix branch's build on the real path a class takes: the window, the separate recorder,
the real microphone and the class app, the run files in the real `%LOCALAPPDATA%\ClassScribe`. It does so without
billing, without Google Drive and without touching your settings, before the branch is finished. Agents use the same kit
for any manual run of a feature branch. (Build plan, section 7.)

## Before the first one

The side copy is always `%LOCALAPPDATA%\Programs\ClassScribe-Dry\ClassScribe.exe`. Add a Bitdefender exception for that
exact path, as for the installed copy (Protection > Advanced Threat Defense > Settings > Manage exceptions; the main
README's "Set up once", step 5): its recorder is a copy of itself started without a window, which Advanced Threat
Defense watches. Since each dry run publishes the file again, Bitdefender may ask again, as it does after a publish of
the installed copy. If the recorder is blocked, ClassScribe records in the window instead, and the check below says so.

## Starting one

Double-click `Start-DryRun.cmd` in Explorer, in the worktree of the branch under test (for a release,
`%LOCALAPPDATA%\csw\<release worktree>\tools\dry-run`). Starting it from Explorer matters: the window and its recorder then
start outside any agent's or editor's job, which would end them with it (under such a job ClassScribe records in the
window, and the check below says so).

Options can follow the `.cmd` from a terminal, as for the script:

```powershell
.\Start-DryRun.cmd                                   # the full dry run: Chrome and the microphone array, about 15 minutes
.\Start-DryRun.cmd -Short -NoMic                     # five minutes of the class alone, stopped by itself
.\Start-DryRun.cmd -Short -Mic "Microphone Array"    # five minutes with the microphone
.\Start-DryRun.cmd -Follows none                     # the microphone written throughout, not only while Chrome listens
.\Start-DryRun.cmd -Expect .srt,.vtt                 # files that must be beside the recording (subtitles, build plan step 5)
.\Start-DryRun.cmd -Setting "ElevenLabs:KeytermsFile=keyterms-islamic-sciences.txt"
.\Start-DryRun.cmd -Exe "$env:LOCALAPPDATA\Programs\ClassScribe\ClassScribe.exe"   # an installed exe, nothing published
```

| Option | What it does |
| --- | --- |
| `-Source <worktree>` | The worktree to publish; by default the one the kit is in. |
| `-Exe <exe>` | Runs that installed exe instead of publishing; nothing is removed at the end. |
| `-Name "Dry run"` | The recording's name. |
| `-App chrome` | The app whose sound is the class (`ClassScribeTestPlayer` with `-Player`). |
| `-Mic "Microphone Array"` | The microphone, by part of its name, as your class launchers name it. |
| `-NoMic` | The class alone. |
| `-Follows <app>` | The app the microphone follows; by default the class app; `none` writes it throughout. |
| `-Class "<saved class>"` | `record --class`: refused by builds without saved classes (they come with build plan step 1). |
| `-Camera "<part>"` | In person with the camera: refused by builds without `record --camera` (build plan step 11). |
| `-Player <clip.wav>` | The test player plays the clip ALOUD as the class; asks you to type PLAY first. |
| `-Setting Section:Name=value` | A setting for this dry run only, as an environment override; several are joined by `;`. |
| `-Expect .srt,.vtt` | Files that must be beside the recording. |
| `-Short` | Five minutes (`--minutes 5`), no pause or close: for a branch whose head moved after its dry run, and hotfixes. |
| `-Keep` | Keeps the side copy afterwards. |
| `-Real` | Sends the recording to ElevenLabs itself with your key, and is billed (about 5 cents for 15 minutes); asks you to type YES. |

## What it does

1. It refuses while a class records or is transcribed, by the run files in `%LOCALAPPDATA%\ClassScribe\runs` (a run in an
   active state whose process is the one that wrote it); it never opens `recording.lock`. It refuses too while a run
   waits for the report a start of ClassScribe makes: an outcome never shown (a class that ended while no window showed
   it), a recording whose recorder ended while it recorded, or a transcription that was interrupted. The side copy makes
   that report itself before it records, so it would mark the outcome seen, rewriting the class's run file, or offer to
   continue or transcribe the class with the stand-in. The script names each such run; open your installed ClassScribe
   by itself (`%LOCALAPPDATA%\Programs\ClassScribe\ClassScribe.exe`, no command), answer its report (C, T or F for a
   recording; Enter leaves it waiting), quit with Q, and start the dry run again. A recording on a drive that is not
   there now, which every start leaves alone, does not count. It refuses too while the side copy still runs.
2. It lists `%LOCALAPPDATA%\ClassScribe`, `%APPDATA%\ClassScribe` and `%USERPROFILE%\ClassScribe` (names, sizes, times),
   and reads each run file's last update (its heartbeat), by which ClassScribe prunes it.
3. It publishes the worktree to `%LOCALAPPDATA%\Programs\ClassScribe-Dry`: `dotnet publish src\ClassScribe -c Release
   -r win-x64 --self-contained false -p:SourceRevisionId=<commit>-dry -p:ContinuousIntegrationBuild=true
   --disable-build-servers`, never single-file and never through an installer, so your class launchers, the installed
   copy and the installer's build folder are not touched. Bitdefender may question the new exe the first time; allow
   that exe if it does.
4. Unless `-Real`, it starts the stand-in for ElevenLabs (below) on a free port of 127.0.0.1, tied to the script's own
   process: closing the dry run's window with its X ends the stand-in too.
5. In its own process only, never at user scope, inherited by the window and its recorder: a dummy `ELEVENLABS_API_KEY`
   (with `-Real`, your user-scope key), `ElevenLabs__BaseUrl` at the stand-in, `Drive__Enabled=false`, and
   `CLASSSCRIBE_SETTINGS_FOLDER` at a `settings` folder of the dry run's own, seeded with a copy of
   `%APPDATA%\ClassScribe\appsettings.json` when you have one. They end with the script.
6. It starts the window: `record "Dry run" --app chrome --mic "Microphone Array" --mic-follows chrome --new --output
   "%USERPROFILE%\ClassScribe dry runs"`, and tells you what to do: play a lecture in Chrome and speak for part of it,
   M once, P twice, close the window once (the recorder goes on), press O in the dry run's window (the side copy opens
   and shows the recording; A in its menu if the menu shows instead), and stop with Q twice there. About 3 seconds after
   the close, a minimized ClassScribe window comes back on the taskbar by itself, as it does in a class (its keys do
   nothing until Enter): close it at the end too, with Enter or its X. The script goes on by itself once the run is over
   and no ClassScribe of the copy is left; until then it names the process ids of the copy's windows still open.
7. It checks: the run file's `home` is `Separate` (else the recorder ran in the window), the run finished as
   `Transcribed`, the transcript is there, the run was marked seen; any `-Expect` files; the recording's log (warnings
   are shown, errors fail the run); and the stand-in's log: one transcript, keyterms sent, no request it does not know.
8. It stops the stand-in, waits until no ClassScribe of the copy is left, removes the side copy (unless `-Keep`), lists
   the three folders again and names each change. Expected: the dry run's run file, lines in `recorder.log` (and
   `recorder.old.log`), the lock file's time, and the files the side copy's recorder prunes as it starts going: run
   files whose last update was over 30 days ago (a run marked seen later keeps its last update, though its file is
   newer), and request files and unreadable files last written over 30 days ago. Anything else, and any change to
   `%APPDATA%\ClassScribe` or `%USERPROFILE%\ClassScribe`, fails the dry run. The recording stays in `%USERPROFILE%\ClassScribe dry runs`, outside
   every recordings folder, so `list` and Drive never see it.
9. It adds a line to `%LOCALAPPDATA%\ClassScribe-DryRuns\runs.log`: the time, PASS or FAIL, the branch, the commit it
   published (and whether uncommitted changes went with it), the build, the options, its folder and the first problem.
   The finish of a release reads it (build plan, amendment 49).

Its own files go to `%LOCALAPPDATA%\ClassScribe-DryRuns\<time>\`: the console, the listings before and after, the
publish log, the stand-in's log, the settings folder and `result.txt`.

## Today's builds and the settings folder

`CLASSSCRIBE_SETTINGS_FOLDER` is read only by builds with saved classes and the settings folder (build plan step 1),
which keep your settings in your own folder. Builds before it, 0.4.0 and 0.4.1 among them, read only the
`appsettings.json` beside their exe: for a dry run, the side
copy's own, which is the branch's file as published, never your installed copy's. The kit still sets the variable (those
builds pass over it) and says which of the two the build reads, by looking for the variable's name in its
`ClassScribe.dll`. A setting a dry run needs, in any build, goes in with `-Setting`, as an environment override, which
every build reads after its files.

## The stand-in for ElevenLabs

`ElevenLabsStub.cs` is a .NET 10 file-based app. The script builds it (`dotnet build ElevenLabsStub.cs`) and runs it;
by hand:

```powershell
dotnet run tools\dry-run\ElevenLabsStub.cs -- 0 "$env:TEMP\stand-in.log" --ready "$env:TEMP\stand-in.ready"
```

It listens on `http://127.0.0.1:<port>/` alone (port 0 takes a free one, which the ready file gives with its process id)
until Ctrl+C; with `--parent <process id>`, as the script starts it, it also ends by itself when that process ends, and
does not start (exit code 4) when that process has ended already. It answers:

- `POST /v1/speech-to-text`: it reads the upload, finds the stretches of the WAV with sound (50 ms frames against about
  -45 dBFS, pauses under 0.6 s bridged, clicks under 0.2 s left out), places a canned text's words on them, one every
  half second, each stretch starting with its own number and second, and answers in ElevenLabs' shape after two seconds
  (`--delay`). Silence gets an empty transcript. So subtitles and track labels get real timings.
- `GET /v1/user/subscription`: a plausible account, whose credits grow by each transcript's seconds.
- Anything else: 404, logged as `UNEXPECTED`.

It logs sizes, times and the number of keyterms; never a header (the key travels in one), never the terms themselves.
The tests compile the same file (`DryRunStandInTests`), so ClassScribe's own client and reader are checked against it.

## The test player

`TestPlayer\` builds `ClassScribeTestPlayer.exe`, which plays a WAV clip ALOUD through Windows' default output from a
process of its own (after a lead-in of silence), so a dry run can record a known class with `--app ClassScribeTestPlayer`.
The script builds and starts it only with `-Player <clip>`, after you type PLAY. It opens no microphone. No test runs it.

## Rules

- Everything in `tools\` is ASCII: Windows PowerShell 5.1 reads a script without a byte-order mark in the ANSI code page.
  `DryRunScriptTests` fails on any byte above 127, and parses every script with Windows PowerShell 5.1's own parser.
- Every catch in a script either throws or begins its comment `# Deliberately not rethrown:` with the reason, as in the
  app; the same test fails on one that does neither.
- The kit changes nothing that is built for a class, and nothing outside its own folders but the run file and the
  recorder's log lines that any run of ClassScribe writes.
- No variable is ever set at user scope; the key is never printed.
