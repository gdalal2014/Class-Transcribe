# ClassScribe

Records the sound your computer plays during a class. When you stop it, it can send the recording
to ElevenLabs (Scribe v2) and save the transcript.

ClassScribe does not record the microphone yet, so your own voice in an online class is not
captured, and neither is a class held in a room. Microphone support is being built.

## Set up once

1. Check the .NET 10 SDK: `dotnet --version` should print `10.x`.
   If it does not: `winget install Microsoft.DotNet.SDK.10`
2. Store your ElevenLabs API key in an environment variable.
   A key that is restricted to Speech to Text is all the app needs.
   ```powershell
   setx ELEVENLABS_API_KEY "your-key"
   ```
   `setx` only reaches programs started after it, so close your terminal and open a new one. In
   VS Code, quit every VS Code window and start VS Code again: a new terminal inside a VS Code that
   is still running keeps the old environment. To check that a terminal sees the key, without
   showing it:
   ```powershell
   if ($env:ELEVENLABS_API_KEY) { 'set' } else { 'missing' }
   ```
3. From this folder, run the tests:
   ```powershell
   dotnet test
   ```
4. For classes, publish a copy of ClassScribe outside OneDrive and start that copy, not `dotnet run`:
   ```powershell
   dotnet publish src/ClassScribe -c Release -r win-x64 --self-contained false -o "$env:LOCALAPPDATA\Programs\ClassScribe"
   ```
   Make a shortcut to `%LOCALAPPDATA%\Programs\ClassScribe\ClassScribe.exe` (see
   [A program to double-click](#a-program-to-double-click)). A recording started from that copy runs
   in a background process of its own, so closing the window does not end it, and rebuilding the
   repo during a class never touches it. Under `dotnet run` the recording runs in the window
   instead, and ClassScribe says so: `dotnet run` ends every program it started when it ends.
5. In Bitdefender, add an exception for that `ClassScribe.exe` under Protection > Advanced Threat
   Defense (ATD) > Settings > Manage exceptions, with the exact path, and add it again after each
   publish, since a changed file may count as new. ATD watches programs that start copies of
   themselves without a window, which is what the background recorder is. If ATD ends the recorder
   anyway, ClassScribe continues the recording by itself while a window shows it, and the next start
   reports it with C to continue it (see [If a recording was interrupted](#if-a-recording-was-interrupted)).

## Use it

```powershell
dotnet run --project src/ClassScribe
```

ClassScribe starts with a menu. Press the number of what you want:

```
  1  Record only
  2  Record and transcribe
  3  Transcribe a recent recording
  4  Transcribe a file
  5  Open the recordings folder
  6  Choose what to record: a device or an app
  A  Show the recording in progress        (only while one records)
  Q  Quit
```

Before the menu, ClassScribe reports what happened while no window showed a recording: a transcript
made in the background (O opens it), or a recording that ended unexpectedly, with C to continue it,
T to transcribe it and F to forget it (see [If a recording was interrupted](#if-a-recording-was-interrupted)).
If a recording is running, the menu shows it at once; Esc comes back to the menu, which then offers
A to show it again. Above the choices, the header names what runs in the background, such as
`Recording now: "Kalam" since 19:00 (00:42:13). A shows it.` or
`Being transcribed in the background: "Fiqh" (uploading 40%).`

- **1 Record only** asks for a name for the recording, such as `Kalam`, then records until you press
  Q twice and keeps the recording, to transcribe later. Esc at the name goes back to the menu. When a
  recording of that name stopped within the last hour (`Recording:ContinueWithinMinutes`) without a
  transcript, it asks first: `C continues it as its next part, N starts a new recording, Esc goes back`.
  The recording runs in a background process of its own (`Recording:SeparateProcess`), and the window
  says `Recording in the background: closing this window does not stop it. Esc leaves this view;
  start ClassScribe again to see it.` Only one recording runs at a time; while one runs, 1 and 2 say
  so, and A shows it.
- **2 Record and transcribe** does the same, then sends the recording to ElevenLabs.
- **3 Transcribe a recent recording** lists the last nine recordings, newest first, each with its
  length, `2 parts` when it was continued after an interruption, and `transcript ready`,
  `answer kept`, `RECORDING NOW` or `no transcript yet`; press one's number. A recording that has a transcript, or an answer ElevenLabs was paid for (see
  [Where things go](#where-things-go)), is not sent again at once, because that is billed again:
  R makes its transcript again for free from ElevenLabs' saved answer (see
  [A transcript again, for free](#a-transcript-again-for-free)), Y transcribes it again, which is
  billed again, and any other key goes back. A recording that is being recorded now, in another
  window, is not sent until it has stopped. A recording whose files cannot be read just now,
  because another program holds one open, is left out of the list with a warning; close that
  program and choose 3 again.
- **4 Transcribe a file** asks for a file: drag it into the window, or type its path, and press
  Enter. A WAV file, 24-bit, floating-point and surround ones included, is first reduced to a small
  16 kHz mono copy; one that another recorder never finished is read to its end, with a warning.
  Any other audio or video file, or a WAV ClassScribe cannot read, is uploaded as it is. One of a
  recording's own `.wav` files, or a recording's folder (which means the recording started last in
  it), gets the same care as in choice 3: R or Y first when it would be billed again, and nothing
  sent while it is being recorded.
- **5 Open the recordings folder** shows it in Explorer.
- **6 Choose what to record** lists the output devices and the apps with sound; see below.

When a choice is done, the menu comes back, after an error too. Q or Ctrl+C leaves it.

Above the choices, the menu shows the settings file it reads (`Settings: ...`), where recordings go
and what is recorded. Each choice reads the settings again first, so an edit counts from the next
choice, without a restart (see [Settings](#settings)). When the file cannot be used, `ERROR` lines
under its name say why. ClassScribe starts all the same, but a choice then starts nothing: it says
what to fix, and the menu comes back. Once the file is fixed, the next choice runs.

If the recordings folder cannot be used, a `WARNING` line right under its name says why:
`Recording:OutputFolder` uses a `%VARIABLE%` that is not set, its drive is not there (an unplugged
USB drive, say), or a file stands where the folder should be. If choice 3 then finds no recordings,
it says `No recordings found.` with the same reason. Fix the setting before you record.

### What is recorded

ClassScribe records what your computer plays, not the microphone. By default it records whichever
output device the class is playing through. It starts on the device with sound, or else on
Windows' default device, and looks at every output device once a second. When the recorded device
has been quiet for two seconds while sound has been playing elsewhere for a second, it moves there
and continues in a new segment file; the segments are joined before upload. A short notification
sound on another device does not move it.

Menu choice 6 lists the connected output devices and the apps with sound open. ClassScribe itself is
never among the apps (a recording's own capture used to show up there), so `Recording:App` cannot
pick it either. What you pick holds until ClassScribe is closed, whatever the settings say:

- **A device**: that device and no other, even when sound plays elsewhere. If it is not connected
  when the recording starts, ClassScribe waits for it.
- **An app**, such as `chrome` or `msedge`: that app's sound alone, whatever device it plays
  through, so a video in another browser or a notification stays out. Windows cannot tell two tabs
  of the same browser apart. If the app has no sound open yet, ClassScribe waits until it plays.
- **A**: back to whichever device the class plays on, which also holds until ClassScribe is closed.

Without a pick from choice 6, `Recording:Device` and `Recording:App` in `appsettings.json` decide,
and they are read again before each choice. To keep a pick for good, set the setting the menu names
after it, or leave both empty for automatic.

The sound is saved in mono. On a surround device the centre channel, where films and players often
put the voice, and the surround channels are mixed in with the front ones.

### While recording

Start recording before the class and leave the window in the background. Play the class as usual,
and keep the sound on: on some sound drivers, muting Windows also silences the recording.
These keys work in the ClassScribe window:

| Key | What it does |
| --- | --- |
| Q, twice | Stops. The first Q only shows `Press Q again within 5 seconds to stop`, so a Q typed into this window by mistake changes nothing. With choice 2 the transcript follows. |
| Ctrl+C, twice | Stops, like Q: the first press only shows `Press Ctrl+C again within 5 seconds to stop`. After the stop, one Ctrl+C cancels the preparation and the upload that follow (within 2 seconds of a stop with Ctrl+C it still counts as part of the stop). The recording is kept either way. |
| P | Pauses, for a break. Nothing is recorded until you press P again. |
| M | Marks the moment. The mark shows in the transcript at that point, as a heading `## [00:42:13] Mark 1`. |
| Esc | Leaves the view of a recording that runs in the background, without stopping or pausing anything: from the menu, the menu comes back; from a command, ClassScribe exits 0 and says `The recording carries on in the background. Start ClassScribe to see it.` In a recording that runs in the window, Esc only says that Q twice stops it. |

The first lines of a recording say where it goes (`Recording "Kalam" to D:\Classes\Kalam`, or
`Continuing "Kalam" as its next part (Kalam_2026-10-15_23-00-02Z, 00:47:10 recorded so far), in ...`),
what is recorded (`Recording from the app "chrome"`, or `the device the class plays on`), the keys
and what closing a window does, the reminder about the lid, and, with `--minutes`,
`This recording stops by itself at 20:30 (--minutes 90); Q twice stops it sooner`. At the end they
say how much was recorded: `Recorded 01:58:10 of audio`, or for a continued recording
`Recorded 00:30:00 in this part, 01:17:10 in all`.

The window's title shows the state at a glance, such as `REC 01:12:40 Kalam | ClassScribe`,
`PAUSED ...`, `! NOT RECORDING (waiting for "chrome") Kalam`, `REC 01:12:40 Kalam (no sound 2 min)`,
`UPLOADING 40% Kalam`, `TRANSCRIBING Kalam`, then `DONE Kalam`, `! FAILED Kalam` or
`CANCELLED Kalam`, with `(stops 20:30)` while `--minutes` or the safety stop will end it, and
`(in this window)` when the recording runs in that window. The taskbar button is green while
recording or uploading, yellow when paused or when something is worth a look, red when something
needs attention or the run failed, and a bar runs back and forth while ClassScribe waits for
ElevenLabs. It flashes once for each new problem that needs attention now, until the window comes
forward; something only worth a look turns it yellow without a flash. Windows Terminal shows the
colour on its tab and its taskbar button, also when ClassScribe was started from a shortcut or a
double-click; the classic console shows it on its own button; the VS Code terminal shows the title
only if its `terminal.integrated.tabs.title` setting includes `${sequence}`, gets no colour, but
its window flashes. Nothing is shown when the output goes to a file. ClassScribe never makes a
sound, and when it exits the window gets its own title back and the colour is cleared.

Those seconds, like the status line and the silence warning, count the time that has really passed,
so a change of the Windows clock neither shortens nor stretches them.

Marks are kept in the recording's `.marks.txt` file, one per line. You can type a label after a
mark's time, such as `00:42:13.250 Definition of jawhar`; the next transcript of that recording then
shows `## [00:42:13] Mark 1: Definition of jawhar`, and you can make that transcript for free (see
[A transcript again, for free](#a-transcript-again-for-free)). Type labels after the class, or
reload the file in your editor before you save it: an editor that saves an older copy would drop
the marks set since. The file also holds notes ClassScribe writes itself, with `~` after the time,
such as `00:47:10.000 ~ Resumed at 23:51 UTC, about 4 minutes not recorded` or
`00:42:10.000 ~ Not recorded: the laptop slept from 23:42:10 to 23:50:31 UTC`. A note shows in
the transcript as a heading in its own words, without a number, and never changes the marks'
numbers; you can type one by hand the same way, with `~ ` after the time. A line that starts with
neither a time nor a note is skipped with a warning, a time written in another script's digits
included.

The status line, every minute, says how much has been captured, and ends with
`; stops by itself at 20:30` while `--minutes` or the safety stop will end the recording. While
paused, a reminder comes every 5 minutes (`Still paused, for 5 minutes now; P records again`), and
from 10 minutes the title says `PAUSED 10 MIN` in red; when sound has kept arriving for 30 seconds
while paused, ClassScribe says `The class seems to be playing while paused; P records again`. At the
start it also reads the free space of the recording's drive and warns when less than 3 hours of
recording fit there; every 5 minutes after that it warns when less than 30 minutes fit
(`Only about 20 minutes of recording still fit on D:\ (2.1 GB free). Free some space there now, or
the recording will stop being saved`), with `DISK NEARLY FULL` in the title. Once a second
ClassScribe also checks that audio keeps arriving. An app's sound arrives all the time, silence included, and a
device's whenever Windows' meter shows sound on it, so a quiet class sets off nothing. When nothing
at all has arrived from an app for 5 seconds, or from a device whose meter has shown sound for 10,
ClassScribe says so and reopens the capture in a new segment file, at most once every 30 seconds:
`No audio has arrived from "chrome" for 5 seconds although it should; the recording has fallen behind the clock. Reopening it`.
For a device the line says `although Windows shows sound playing there` instead. An app is watched
during a pause as well; the line then says the capture is reopened so that the recording can go on
when you press P again.

### The background recorder

With the published copy, a recording started from the menu or the command line runs in a
ClassScribe process of its own, without a window: the same `ClassScribe.exe`, started as
`ClassScribe.exe --recorder <run id>`. The window hands it the settings it has just read and
checked, so the recorder never reads `appsettings.json` itself, and an edit made during a class
counts from the next recording. The recorder takes the API key from the environment it inherited
from the window, and from nowhere else; nothing on disk holds the key. The window shows the
recording through a link to the recorder and passes the keys on to it, so closing the window, a
crash in it, or a key typed into it by mistake can no longer end a class. The window says
`Recording in the background: closing this window does not stop it. Esc leaves this view; start
ClassScribe again to see it.`

To see the recording again, start ClassScribe: the menu shows it at once (what it has logged so far,
then what happens), and A shows it again after Esc. `record` with the same name shows it too
(`"Kalam" is being recorded already, since 19:00 (00:42:13); showing it.`), and so does the
hidden command `ClassScribe attach`, which the window that comes back by itself runs as
`ClassScribe attach --alert <run id>`. Up to four windows can show one recording, and the keys
from any of them reach it. If a window loses its link to the recorder it tries for 10 seconds to
make it again, then says `Lost the connection to the recording; it carries on. Start ClassScribe
again to see it.`; if the recorder sends nothing for 10 seconds while its process runs, the window
says `The recorder is not answering (process 1234, started 19:00). Its audio so far is on disk. W
keeps waiting, S stops it.`, and S stops the recording through a signal that does not need the
link.

Only one recording runs at a time, through a lock that is held only while it captures, so the next
class can start while the last one is still being uploaded. The menu's header then says
`Being transcribed in the background: "Fiqh" (uploading 40%).`, and that recording cannot be sent
again until it is done (`... is being transcribed in the background; its transcript will appear
in ...`). A second recording is refused: `Only one recording at a time: "Fiqh" has been recording
since 19:00 (00:42:13). Start ClassScribe to see it, or stop it first.` (in the menu, `A shows it.`),
with exit code 1 on the command line.

At the end, the window that started the recording opens the folder and the transcript as the
settings say; when it is gone, the last window you opened to see the recording does. With no window
showing it, the recorder opens them itself (`No ClassScribe window was showing this recording at its
end, so the recorder opens the results itself`), so the transcript appearing is the sign that the
work is done. `--no-open` opens nothing anywhere. A window that came back by itself (below) never
opens anything, and the next start of ClassScribe then shows the outcome once, with O to open the
transcript (see [If a recording was interrupted](#if-a-recording-was-interrupted)).

The recording runs in the window instead, as it did before, and the window says so first, when:

- `Recording:SeparateProcess` is `false`: `Recording in this window (Recording:SeparateProcess is
  false): closing it ends the recording; the audio so far is kept.`
- ClassScribe was started under `dotnet run`, or in another way that ends every program it started
  when it ends: `ClassScribe was started in a way that ends every program it starts when it ends, as
  dotnet run does. So this recording runs in this window: closing it ends the recording, and the
  audio so far is kept. For classes, start ClassScribe.exe itself (README, Set up once).`
- `%LOCALAPPDATA%\ClassScribe` cannot be written: the reason, then `One recording at a time and
  the report after a crash are not available this time.`
- The recorder would not start twice in a row: the reason, `Starting the recorder once more.` after
  the first failure, then `Recording in this window instead. ...` and `If Bitdefender showed a
  notice, add the exception described in the README.`

A recording in the window adds `(in this window)` to the title, and everything else works the same:
one recording at a time, the report after a crash (which then says `It ended when its window was
closed`), the keys, the laptop kept awake.

If you close the last window showing a recording while it records, the recording goes on, and 3
seconds later a minimized ClassScribe window comes back on the taskbar without taking the focus,
flashing once, so that the recording is not forgotten (`Recording:ReopenWindow`). It comes back the
same way, at once, when something needs attention (nothing recorded for 15 seconds, say) or when the
run fails, and once something only worth a look, such as no sound, has lasted 2 minutes (with the
silence warning at 60 seconds, that is about 3 minutes of silence). One such window at a time, and
at most one every 2 minutes. Its first lines say why it opened, such as `The window was closed; the
recording goes on. A ClassScribe window comes back, minimized, so that the recording is not
forgotten; its keys do nothing until Enter is pressed in it`. That window ignores every key until
you press Enter in it (`ClassScribe opened this window by itself. Keys do nothing here until you
press Enter.`, and `Press Enter to control the recording from this window.` when a key is pressed),
so text typed into it by mistake cannot stop or pause the class; after Enter, Q, P, M, Esc and
Ctrl+C work there as in any window. If you close it again within 2 minutes, no window comes back
for a mere close again in that recording (`The ClassScribe window that came back was closed within
2 minutes, so a closed window brings none back again in this run; something that needs attention
still does`). Leaving the view with Esc brings no window back. With `Recording:ReopenWindow` set to
`OnProblems` only a problem brings a window back; with `Never`, none does, and the next start of
ClassScribe reports what happened.

### Keeping the laptop awake

From the start of a recording until its transcript is saved (with record only, until its files are
closed), and while a recording or a file is transcribed, ClassScribe asks Windows to keep the laptop
awake. `powercfg /requests`, in a terminal run as administrator, lists the request as
`ClassScribe is recording a class` or `ClassScribe is transcribing a recording`. Windows ends the
request when ClassScribe ends, however it ends. The screen is kept on as `Recording:KeepScreenOn`
says:

- `OnBattery`, the default: only while the laptop runs on battery. There a laptop with Modern
  Standby goes into standby once its screen turns off, whatever a program asks, so the screen is
  what keeps it recording. On mains, keeping the laptop awake is enough, and the screen may turn
  off. ClassScribe looks again every 30 seconds, so the screen follows when you unplug the laptop
  or plug it in.
- `Always`: on battery and on mains.
- `Never`: not at all. On battery the laptop may then go into standby a few minutes after your last
  key press, and nothing is recorded until it wakes.

Dim the screen by hand if you like. If Windows refuses a request, ClassScribe says so and carries
on all the same; the message advises keeping the laptop plugged in, with sleep set to Never in
Settings > System > Power & battery.

Closing the lid, the power button and Start > Sleep still put the laptop to sleep, which no program
can prevent; a recording starts with the reminder `Keep the lid open and the laptop plugged in;
closing the lid still puts it to sleep`. If the laptop slept during a recording, or Windows paused
ClassScribe, for 3 seconds or more, the log says when, in local time, such as
`The laptop slept from 19:42:10 to 19:50:31; nothing was recorded then` or
`ClassScribe was held up from 19:42:10 to 19:42:28 (Windows paused it); nothing was recorded then`.
The silence warning and the check that audio keeps arriving count from the end of such a gap, so the
time asleep is not taken for a fault of the device.

### Where things go

Recordings and transcripts go to the folder set as `Recording:OutputFolder` in `appsettings.json`,
which is `%USERPROFILE%\ClassScribe` unless you change it. Each name gets a folder, and every file of
a recording carries the name and the time the recording started, in UTC (the `Z`):

```
%USERPROFILE%\ClassScribe\
    Kalam\                                       one folder per name, shared by its recordings
        Kalam_2026-10-14_19-30-00Z.wav           the audio as captured (kept)
        Kalam_2026-10-14_19-30-00Z_part2.wav     only after a device switch, a failure or 1500 MB
        Kalam_2026-10-14_19-30-00Z.marks.txt     the marks set with M, and ClassScribe's own notes, if any
        Kalam_2026-10-14_19-30-00Z.log           everything the recording's runs logged
        Kalam_2026-10-14_19-30-00Z.md            the transcript
        Kalam_2026-10-14_19-30-00Z.json          the full ElevenLabs response, with word timings
        Kalam_2026-10-14_19-30-00Z.upload.wav    the 16 kHz copy that is uploaded, while it is needed
        Kalam_2026-10-14_19-30-00Z.unread.json   only when a paid answer did not become a transcript
    Lecture 5\                                   a transcribed file; the file itself stays where it is
        Lecture 5_2026-10-15_09-00-00Z.md
        Lecture 5_2026-10-15_09-00-00Z.json
```

The transcript is a Markdown file: the name as its title, when it was recorded, then paragraphs
that start with their `[hh:mm:ss]` position, and the marks as headings, so they show in the outline
of any Markdown viewer (VS Code, Obsidian). It is plain text underneath, so Notepad opens it too.
ElevenLabs itself answers with JSON (the text and every word's timing); that is kept as the `.json`.

When a transcript is ready, its folder and the transcript open by themselves. A transcript is never
overwritten: transcribing a recording again, or making its transcript again for free, adds
`Kalam_2026-10-14_19-30-00Z (2).md`, then `(3).md`, even when two ClassScribe windows save at once.

The `.upload.wav` is the smaller copy (16 kHz mono) that is sent to ElevenLabs. It is removed when
the transcript is saved, and also after a failed or cancelled upload, unless
`Recording:KeepUploadFile` is on; if another program holds it open, ClassScribe only warns, and you
can delete it later. A copy left behind by a ClassScribe that was closed during the upload is
replaced when that recording is transcribed again.

ElevenLabs charges for every transcript it sends back. When such an answer arrives but no
transcript can be made from it, ClassScribe says so and keeps the answer. One that cannot be read,
or cannot be saved as the `.json`, is kept as `<name>_<time>Z.unread.json` beside the recording (a
second one gets `(2)` before `.unread.json`), or under the same name in the recordings folder when
the recording's folder will not take it. When the `.json` was saved but the transcript could not be
made from it, that `.json`, with no `.md` beside it, is the kept answer. Either way its `"text"` is
the transcript's text, so look there before transcribing again, which is charged again. Menu
choice 3 shows a recording that has such an answer, and no transcript, as `answer kept`. If the
answer cannot be kept anywhere, ClassScribe says that too. The `Transcript received` line in the
log, and that message, give the answer's ElevenLabs transcription ID: ElevenLabs keeps the
transcript under it, and
[its API can return it again](https://elevenlabs.io/docs/api-reference/speech-to-text/get).

A name is made safe for Windows: characters Windows does not allow in a file name are left out, and
a name Windows keeps for a device, such as `NUL` or `COM1`, gets an underscore (`NUL_`). A file with
nothing usable in its name, such as `.mp3`, gets an `Untitled` folder.

The positions are positions in the recording. While nothing at all is playing (a paused video,
for instance) Windows usually hands over no audio, so a pause can be shorter in the recording
than it was in the room. Time paused with P is left out too.

The `.log` beside a recording holds every line its runs logged, from the first line to the
transcript, in the background or in the window alike; a run that continues the recording adds to
it. It is the record of a class after a crash, a closed window or a restart, and it can be read
while ClassScribe writes it.

#### ClassScribe's own folder

The bookkeeping of the background recorder lives in `%LOCALAPPDATA%\ClassScribe`, which never roams
or syncs, and nothing in it holds the API key:

```
%LOCALAPPDATA%\ClassScribe\
    recording.lock                   held open while a recording captures; Windows frees it when the process ends
    recorder.log                     the recorder's lines until the recording's folder is known; recorder.old.log
                                     keeps the generation before, at about 1 MB
    runs\<run id>.json               one file per run: its state, a heartbeat every 10 seconds, and how it ended
    runs\<run id>.request.json       what the window handed the recorder; deleted once the recorder has read it
```

A run id is the start time in UTC and eight random digits, such as `20261015-230002Z-1f3a9c07`. A
run that needs nothing more (seen, continued or forgotten) is deleted 30 days after its last update.
The recorder also runs in this folder, so it holds nothing under OneDrive or in the repo open.

### Without the menu

```powershell
ClassScribe record "Kalam" --device auto --output "D:\Classes"          # record, then transcribe
ClassScribe record-only "Kalam" --app chrome --output "D:\Classes"      # record only
ClassScribe transcribe "C:\path\to\recording folder or file"
ClassScribe rebuild "C:\path\to\recording folder or file"
ClassScribe list                                                        # the recordings, newest first
ClassScribe devices                                                     # the devices and apps with sound
ClassScribe --help                                                      # the commands; also record --help and so on
```

`ClassScribe` here stands for the published `ClassScribe.exe` (see
[Set up once](#set-up-once)), by its full path when it is not in the current folder, as in
`& "$env:LOCALAPPDATA\Programs\ClassScribe\ClassScribe.exe" record ...`. With
`dotnet run --project src/ClassScribe -- record ...` the same lines work, but the recording then
runs in the window, and closing the window ends it.

In short: `record "<name>" SOURCE --output "<folder>" [--minutes <n>] [--no-open] [--continue | --new]`,
`record-only` with the same options, `transcribe <path> [--output "<folder>"] [--again] [--no-open]`,
`rebuild <path> [--no-open]`, `list [--output "<folder>"]` and `devices`, where the parts in
brackets may be left out, and SOURCE is `--device "<part of its name>"`, `--device auto` or
`--app <process>`. `<command> --help` (also `-h`, `-?`, `/?` or `help <command>`) lists a command's
options with examples.

For `record` and `record-only` the name, a source and `--output` are required, and nothing is ever
asked: every missing piece and every value ClassScribe cannot use is named at once, followed by that
command's help, with exit code 2, before the settings are read and before anything starts.
`transcribe` on a recording's folder takes the recording started last in it; on one of its files,
that whole recording; any other audio or video file, on its own, as menu choice 4 does. Given a
folder, it also names the other recordings there that have no transcript, each with its length,
the local time it started and the command that transcribes it (or, for one whose paid answer is
kept, the free `rebuild` command): `Also in this folder, without a transcript: ...`. It does so
before the upload and again after it, so that a class split in two by a crash is not half
transcribed. When `--output` names the same folder as `Recording:OutputFolder`, `record` and
`record-only` give the same warning as the menu about a folder that cannot be used, before they
record.

A recording of the same name that is running already is shown instead of started; another name
gives `Only one recording at a time: ...` and exit code 1. `record` with a name that stopped within
`Recording:ContinueWithinMinutes` without a transcript starts a new recording at once and says how
to continue the other one instead (`--continue`); it never asks and never waits.

On the command line nothing is asked, so `transcribe` refuses two kinds of recording, with exit
code 1 and a message that says what to do instead:

- one that already has a transcript, or an answer ElevenLabs was paid for: transcribing it again is
  billed again. The message gives the free `rebuild` command (see
  [A transcript again, for free](#a-transcript-again-for-free)), then the same `transcribe` command
  with `--again`, which sends it all the same;
- one that is still being recorded, in another window: transcribe it once it has stopped.

A file dropped on `ClassScribe.exe` counts as the command line too.

| Option | With | What it does |
| --- | --- | --- |
| `--device "<part of its name>"` | `record`, `record-only` | Records that output device and no other, found by part of its name, such as `"Pixel Buds"`. `--device auto` records whichever device the class plays on. |
| `--app <process>` | `record`, `record-only` | Records one app's sound, whatever device it plays on, by the app's process name, such as `chrome` (`chrome.exe` works too). One source only: `--device` and `--app` cannot go together. |
| `--output "<folder>"` | `record`, `record-only`, `transcribe`, `list` | The recordings folder: the recording gets a folder of its name inside it. Required for `record` and `record-only`. For `transcribe` it is used only for a file that is not a recording's; a recording's transcript goes beside the recording. |
| `--minutes <n>` | `record`, `record-only` | Stops by itself after that many minutes, counted from the start with pauses included, such as `90` or `1.5` (a dot, never a comma; more than 0 and at most 1440). Q twice still stops it earlier. |
| `--continue` | `record`, `record-only` | Continues, as its next part, the recording of that name that stopped within `Recording:ContinueWithinMinutes` without a transcript; when there is none, says so and starts a new one. |
| `--new` | `record`, `record-only` | Starts a new recording without a word about one that could be continued. Not with `--continue`. |
| `--again` | `transcribe` | Transcribes a recording that already has a transcript or a paid answer. This is billed again. |
| `--no-open` | `record`, `record-only`, `transcribe`, `rebuild` | Opens nothing at the end, whatever `Recording:OpenFolderWhenDone` and `Recording:OpenTranscriptWhenDone` say. |

An option may come before or after the path or the name, in any letter case; `--` ends the options,
for a name that starts with `--`. An argument that starts with `@` is a path, not a file of
arguments, and a first word in square brackets is a path from the current folder. A line
ClassScribe cannot read starts nothing: the mistakes are named, then that command's help is shown.
The menu is shown only when someone can type into the window: started without a command from a
script, ClassScribe says so, shows the help and exits 2.

`list` prints every recording under the recordings folder, newest first, one line each
(`Recordings in D:\Classes, newest first (times are local):`): its name, when it started in local
time, its length, `2 parts` when it was continued, `transcript`, `transcript (2)` for one made twice,
`answer kept` or `no transcript yet`, and `RECORDING NOW`, `TRANSCRIBING` or `ENDED UNEXPECTEDLY`
where that applies; then a line for each recording being transcribed in the background. It changes
nothing. `devices` prints the output devices, marked `default` or `playing`, then the apps with
sound (`chrome  (app on Speakers (Realtek(R) Audio), playing)`), each with the `--device` or `--app`
option that records it, leaving out ClassScribe itself; it opens no device, and ends with an
example `record` line.

At the end, ClassScribe hands Windows an exit code, which a script can check (in PowerShell,
`$LASTEXITCODE` holds it):

| Exit code | Meaning |
| --- | --- |
| 0 | Done: the recording (`record-only`) or the transcript (`record`) was saved; also after Esc left the view of a recording that carries on. |
| 1 | It failed, or it was refused, and the message says why: settings ClassScribe cannot use, nothing recorded, a transcript that failed (the audio is kept), another recording running, a recorder that was lost and not continued, a recording that already has a transcript or a paid answer (without `--again`), a recording being recorded now, `rebuild` without a saved answer. |
| 2 | A mistake on the command line, named with that command's help: a missing name, source or `--output`, two sources, a value that cannot be used, an unknown word, or a path that is not there. Nothing was started. |
| 130 | Cancelled with Ctrl+C. |
| none | The window was closed while the recording ran in the background; the next start reports how it ended. |

When `record-only` ends, its last message gives the command that transcribes the recording; so does
`record` when there is no API key. When the transcript after `record` fails, or you cancel it, the
command ends the `WARNING` line that starts `The audio is safe in`, just above the final error (or
`Cancelled`). When ElevenLabs had already charged for an answer that is kept, two lines follow that
warning instead: the `rebuild` command that lays that answer out for free, then the
`transcribe ... --again` command for a new try, which is billed again. A recording with nothing
audible in it gets no command, as trying again would find the same silence, and neither does one
longer than ElevenLabs takes in one file, which would be refused again. Menu choices 1 and 2 do the
same. Each command names the program by its full path, so you can paste it into PowerShell as it
is, from any folder.

### If a recording was interrupted

A recording that runs in the background keeps a run file in `%LOCALAPPDATA%\ClassScribe\runs`, with
a heartbeat every 10 seconds. When its process is gone while the file still says it was recording,
every start of ClassScribe reports it until you deal with it:

```
The recording "Kalam" (Kalam_2026-10-15_23-00-02Z) ended unexpectedly at about 19:47, 00:47:10 in. Its audio is kept.
If you did not stop it, check Bitdefender's Notifications (Protection > Advanced Threat Defense).
  C  continue it now as its next part     T  transcribe what was recorded
  F  forget this                          Enter  go on to the menu
```

Enter goes on; so do Esc and Ctrl+C.

The second line names the cause when it is known: `Windows shut down or signed you out at about
19:47`, `Windows restarted at about 19:47`, `It ended when its window was closed at about 19:47`
(a recording that ran in the window), or `It stopped because of an error at about 19:47; <base>.log
says which`. C is offered while the recording can still be continued (within
`Recording:ContinueWithinMinutes`, with no transcript and no paid answer kept, shorter than 9 and a
half hours, and no other recording running) and continues it with the run's own name, source and
folder. On the command line the report prints the `record ... --continue` and `transcribe` commands
instead, then `To forget it: start ClassScribe without a command, then press F.`, and asks nothing.

A transcription that was interrupted (the background process ended during the upload) is reported
the same way, with T and F: `The transcript of "Kalam" (Kalam_2026-10-15_23-00-02Z) was not
finished; its audio is complete. If the upload had finished, ElevenLabs may bill it again.` So is
an end nobody saw, once: `"Kalam" was transcribed in the background: <path>. O opens it; any other
key goes on.`, `"Kalam" was recorded in the background: <folder>.` with the command that
transcribes it, or `"Kalam" ended in the background: <why>` with the command to try again. A run
that has been dealt with since (continued by a later run, transcribed, or its audio deleted) is
not reported, and one whose recording is on a drive that is not there now, such as an unplugged USB
drive, waits for a start that finds the drive.

A recording continued as its next part stays one recording: the new audio goes into the next part
file, the marks go on, and the transcript says `Recorded 15 October 2026, 23:00 UTC, in 2 parts.`
with a heading `## [00:47:10] Resumed at 23:51 UTC, about 4 minutes not recorded` at the join. A
recording that can no longer be continued, such as one whose audio was moved, costs nothing: the
run warns (`Could not continue ...; recording "Kalam" as a new recording instead`) and records a
new one.

While a window shows a recording whose background process ends unexpectedly, the window continues
the recording by itself within a few seconds (`The recorder ended unexpectedly at 19:47:12;
continuing the same recording.`), with the rest of `--minutes` when there was one, the window that
came back by itself included, with its keys still waiting for Enter. After a second loss within 10
minutes, which most likely means Bitdefender is ending the recorder, a window you started continues
in itself (closing it then ends the recording), while a window that came back by itself declines
(`ClassScribe opened this window by itself, so the recording is not continued in it. Its audio so
far is kept; start ClassScribe to continue it, or to transcribe it.`) and leaves the report for the
next start.

A restart, shutdown or sign-out you start during a recording or its upload makes Windows ask first
and name ClassScribe (`Recording:BlockShutdown`); if Windows ends the session anyway, the files are
finished and the next start offers to continue. A recording also stops by itself after
`Recording:StopAfterHours` (9 hours), with a warning 15 minutes before, because ElevenLabs takes at
most 10 hours in one file.

### A transcript again, for free

ClassScribe keeps ElevenLabs' answer (the `.json`) beside each transcript, so a transcript can be
made again without sending anything or paying again: after you have typed labels into the marks
file, say, or changed the `Transcript` settings. Press R in menu choice 3 or 4 for a recording that
has a transcript, or run `rebuild`:

```powershell
dotnet run --project src/ClassScribe -- rebuild "C:\path\to\recording folder or file"
```

`rebuild` takes a recording's folder (its newest recording with a saved answer, even one whose audio
has been deleted; a newer recording it passes over is named, with the reason), any file of a
recording (`.wav`, `.marks.txt`, `.md`), or an answer saved as `.json`. It lays out the recording's
newest saved answer, or the `.json` given, needs no API key, uploads nothing, and says so:
`Rebuilt from Kalam_2026-10-14_19-30-00Z.json into Kalam_2026-10-14_19-30-00Z (3).md; nothing was uploaded`.
The new transcript goes beside the old ones as the next free copy, with the marks (labels typed in
since included) and the `Transcript` settings as they are now; nothing is ever overwritten. A
recording that was never transcribed has nothing to lay out yet:
`... has no saved answer from ElevenLabs yet; transcribe it first`.

For a recording whose paid answer is kept without a transcript (`answer kept`), R lays that answer
out. An answer ClassScribe set aside as `.unread.json`, because it could not be read or saved in its
place, is used only when you give `rebuild` that very file: given the recording, `rebuild` names it
and gives the command. The words are ElevenLabs' answer as it was: to use a changed glossary, the
recording has to be transcribed again, which is billed again.

## Dry run before the first real class

Take two minutes to check the whole path on your machine:

1. Start ClassScribe, press 2 and type a name, such as `Dry run`.
2. Play any video with speech for 30 seconds.
3. Press Q twice and read the transcript when it opens.

## How the recording is protected

- Each packet of audio goes to Windows as it arrives, and the file's header is brought up to date
  every 5 seconds. If the app is killed, or Windows shuts down or signs you out during the class,
  at most a fraction of a second is lost; after a power cut, at most those 5 seconds. Transcribing
  reads such a file to its very end, whatever its header says.
- Pausing with P saves the file first, so a crash during a break loses nothing recorded before it.
- The transcript step starts only after the audio file is closed. If the upload fails, the audio is
  untouched and the class can be transcribed again.
- When the sound moves to another output device (headphones plugged in, Bluetooth dropping),
  ClassScribe follows it within a few seconds and continues in a new segment file. The segments are
  joined before upload.
- If the device fails, it is reopened. While there is nothing to record (no output device, a chosen
  device not connected, a chosen app not playing) or the device cannot be opened yet, ClassScribe
  looks again every second and says so at once, then every 30 seconds. That holds from the very
  start: only a device whose sound format ClassScribe cannot read keeps a recording from starting.
- The once-a-second look at the output devices and the apps lets go of every Windows object it uses
  straight away, so ClassScribe's handles and memory stay flat in Task Manager through a long class.
  Before, each look left a handle behind until .NET tidied up, about 1,000 every 17 minutes.
- If the disk cannot be written, ClassScribe says so and continues in a new file. If writing fails
  again straight away (a full disk, say), it waits 30 seconds before each new try, and the audio of
  that time is lost. A file left with no audio in it is removed. Only audio that reached the disk is
  counted, so the marks stay in step with what is transcribed.
- If Windows holds the capture up for longer than it keeps audio waiting (one second), ClassScribe
  warns that the recording probably has a gap there.
- A capture that stays open but stops handing over audio, which Windows reports in no other way, is
  found within seconds and reopened (see [While recording](#while-recording)).
- The laptop is kept awake while ClassScribe records and uploads, and a time it slept anyway, or
  Windows paused ClassScribe, is logged (see [Keeping the laptop awake](#keeping-the-laptop-awake)).
- If a sound driver hangs as you stop, ClassScribe waits for it at most 20 seconds, then stops
  without it. Everything recorded so far is kept.
- It warns when it has heard nothing for 60 seconds, except while paused.
- A recording of pure digital silence (nothing but exact zeros, as Windows hands over while muted)
  is not uploaded, so it costs nothing.
- An existing recording is never overwritten.

## The glossary

`keyterms.txt` (next to the app) lists terms ElevenLabs should spell as written, one per line.
It ships with a starter list for an Ashʿarī kalām class in IJMES transliteration. Technical terms,
school names among them, are fully transliterated. Names of people and titles of books follow IJMES
in leaving out macrons and dots while keeping ʿayn and hamza, as in `al-Ghazali` and
`al-Iqtisad fi al-Iʿtiqad`
([IJMES guide](https://www.cambridge.org/core/journals/international-journal-of-middle-east-studies/information/author-resources/ijmes-translation-and-transliteration-guide)).
Edit it freely:

- up to 1000 terms, each under 50 characters and at most 5 words, without `< > { } [ ] \`
- parts joined by a hyphen, a dash, a slash or another mark count as separate words: `Ahl al-Sunna`
  is three, and `Ahl al-Sunna wa-l-Jamāʿa` is six, one too many. Apostrophes, ʿayn and hamza stay
  inside their word.
- a term that breaks a rule is skipped, with a warning that says why; the others are still sent
- lines starting with `#` are comments
- if a term keeps coming out wrong, try a plainer spelling of it

After changing the glossary you can transcribe an old recording again (menu choice 3, then Y).
That is billed as a new transcription.

## Settings

`appsettings.json` holds the settings. ClassScribe reads the copy next to the app, and the menu
names it at the top (`Settings: ...`). A published app reads the copy beside `ClassScribe.exe`. With
`dotnet run` it is the copy in `src\ClassScribe\bin\Debug\net10.0`, which a build makes from
`src\ClassScribe\appsettings.json` and replaces only when that file is the newer of the two. So edit
`src\ClassScribe\appsettings.json` and build again (`dotnet run` builds each time it starts), or edit
the copy itself, which a build then leaves alone until the project's file changes again. While the
project's file is newer than the copy and differs from it, the menu and each command say so in a
`WARNING` line, and the change does not count until the build.

ClassScribe reads the file again before each menu choice and each command, and checks it before
anything uses it, so an edit counts from the next choice, without a restart. A recording that is
running keeps the settings it started with. A value ClassScribe cannot use stops that choice or
command before it records or uploads anything, with a message that names the setting and says what
it must be, then `Nothing was started. Fix the settings above in ..., then choose again` (for a
command, `then run the command again`, with exit code 1). The menu shows the problems at the top as
well, and comes back after each refused choice. Such a file does not keep ClassScribe from starting:
until it is fixed, the settings stay as they were last read, or the defaults if they never could be
read, and fixing the file is all it takes. If the file is deleted or renamed while ClassScribe runs,
the settings also stay as they were, and choices are refused until it is back; to go back to the
defaults on purpose, start ClassScribe again without the file.

An environment variable that sets a value ClassScribe cannot use, such as `Recording__FlushSeconds`
set to `abc`, is different: it cannot be fixed while ClassScribe runs, so ClassScribe does not start,
and says which variable it is (`An environment variable sets a value ClassScribe cannot use ...`).

Two things only bring a warning, given once, at the first choice or command: an API key written into
the settings (see below), and a `Recording:OutputFolder` inside OneDrive, which uploads a recording
again and again while it grows, and can hold files ClassScribe needs to delete.

The file is JSON, which has two traps:

- In a path, write each backslash twice, as in `"D:\\Classes"`, or use forward slashes, as in
  `"D:/Classes"`. A single backslash starts a special character, such as `\t` for a tab.
- Write numbers with a dot (`1.5`, not `1,5`), and on or off as `true` or `false`.

The ones you are most likely to change:

| Setting | Default | Meaning |
| --- | --- | --- |
| `Recording:OutputFolder` | `%USERPROFILE%\ClassScribe` | Where recordings and transcripts go, one folder per name. Environment variables work, and quotes around the path are ignored. A relative path is taken from your user folder, so `Classes` means `%USERPROFILE%\Classes`. A folder inside OneDrive brings a warning. |
| `Recording:Device` | empty | Part of the name of the output device to record, such as `Pixel Buds`. Empty means the device the class plays on. |
| `Recording:App` | empty | The app whose sound to record, by its process name, such as `chrome`. Overrides `Recording:Device`. |
| `Recording:SilenceWarningSeconds` | 60 | Warn after this much silence. At least 1. |
| `Recording:KeepUploadFile` | false | Keep the 16 kHz copy that was uploaded, after a failed or cancelled try as well. When off, the copy is removed after every try, whether it worked or not. |
| `Recording:OpenFolderWhenDone` | true | Open the folder in Explorer when the recording or transcript is ready. |
| `Recording:OpenTranscriptWhenDone` | true | Open the transcript when it is ready. |
| `Recording:KeepScreenOn` | `OnBattery` | When the screen is kept on while ClassScribe records or uploads: `OnBattery` (only on battery, where it is what keeps the laptop out of standby), `Always` or `Never`. The names are taken in any letter case; a number is refused. See [Keeping the laptop awake](#keeping-the-laptop-awake). |
| `Recording:SeparateProcess` | true | Record in a background ClassScribe process, so that closing the window does not end the class. `false` records in the window, as before. Under `dotnet run` the recording runs in the window either way. |
| `Recording:ReopenWindow` | `OnCloseAndProblems` | When ClassScribe brings a minimized window back by itself, without taking the focus, while no window shows the recording: `OnCloseAndProblems` (3 seconds after the last window was closed while it records, at once when something needs attention or the run fails, and once something worth a look has lasted 2 minutes), `OnProblems` (only for those problems, never for a close) or `Never`. The names are taken in any letter case; a number is refused. That window ignores every key until Enter. See [The background recorder](#the-background-recorder). |
| `Recording:BlockShutdown` | true | While a recording or its upload runs, a restart, shutdown or sign-out you start asks first and names ClassScribe; a forced shutdown is not held. |
| `Recording:ContinueWithinMinutes` | 60 | Offer to continue a recording of the same name that stopped this recently without a transcript. 0 never offers; at most 1440. |
| `Recording:StopAfterHours` | 9 | The safety stop, with a warning 15 minutes before, because ElevenLabs takes at most 10 hours in one file. 0 turns it off; at most 9.5. |
| `ElevenLabs:LanguageCode` | empty | Empty lets ElevenLabs detect the language. |
| `ElevenLabs:NoVerbatim` | false | Drop filler words and false starts. |
| `ElevenLabs:KeytermsFile` | `keyterms.txt` | The glossary. A relative path is taken from the app's folder; environment variables work, and quotes are ignored. Empty sends no glossary. |
| `ElevenLabs:TimeoutMinutes` | 60 | The least time one try is given: the upload, the transcription and the whole answer. A longer recording gets more; see [How long an upload may take](#how-long-an-upload-may-take). From 1 to 1440. |
| `ElevenLabs:SlowestUploadMbps` | 1 | The slowest upload speed, in megabits per second, that the time limit allows for. If your line uploads more slowly, set it a little below that speed. From 0.1 to 1000. |
| `Transcript:IncludeTimestamps` | true | Start paragraphs with `[hh:mm:ss]`. |
| `Transcript:ParagraphPauseSeconds` | 2.0 | A pause between words this long, in seconds, starts a new paragraph. 0 or more; 0 turns these breaks off. |
| `Transcript:MaxParagraphCharacters` | 900 | A longer paragraph is split at the next sentence end. 0 or more; 0 turns this off. |
| `Logging:Console:FormatterName` | `plain` | One line per message. Set to `simple` to see full stack traces. |

The file has a few more that rarely need changing, checked the same way: `Recording:FlushSeconds`
(5, at least 1), `Recording:StatusIntervalSeconds` (60, at least 1), `Recording:MaxSegmentMegabytes`
(1500, from 10 to 4000), `ElevenLabs:ModelId` (`scribe_v2`, not empty), `ElevenLabs:TagAudioEvents`
(false; true marks laughter and other sounds), `ElevenLabs:MaxAttempts` (3, from 1 to 10) and
`ElevenLabs:RetryDelaySeconds` (5, from 0 to 3600). The `Logging` section is read again and checked
like the rest: each log level, under `Logging:LogLevel` (where `Default` is `Information`) or under a
log's own section, such as `Logging:Console:LogLevel`, must be `Trace`, `Debug`, `Information`,
`Warning`, `Error`, `Critical` or `None`.

The API key is deliberately not in this file. It is read from `ELEVENLABS_API_KEY` alone: a key
written into `appsettings.json` is ignored, and ClassScribe warns you to remove it, so that it
cannot be shared by mistake.

### How long an upload may take

Each try of an upload has a time limit, for the upload, the transcription and the whole answer. It
is at least `ElevenLabs:TimeoutMinutes`, and grows with the recording: 10 minutes, plus the upload at
`ElevenLabs:SlowestUploadMbps`, plus a quarter of the audio's length, rounded to the minute. At the
defaults an hour of class keeps its 60 minutes, while two hours (an upload of 230.4 MB) get 71, four
hours 131 and six hours 192. The `Uploading ...` line gives the size, in MB of a million bytes, and
the limit, such as
`Uploading Kalam_2026-10-14_19-30-00Z.upload.wav (230.4 MB) with ... keyterms; time limit 71 minutes`.

A recording longer than 9 and a half hours is not sent at all, because ElevenLabs takes at most 10
hours per file: nothing is prepared or uploaded, and the message says how to send it in pieces.

## A program to double-click

Publish ClassScribe as a folder outside OneDrive, as [Set up once](#set-up-once) says:

```powershell
dotnet publish src/ClassScribe -c Release -r win-x64 --self-contained false -o "$env:LOCALAPPDATA\Programs\ClassScribe"
```

Keep `ClassScribe.exe` together with the files beside it, and make a shortcut to it: right-click
`ClassScribe.exe`, then Show more options, Send to, Desktop (create shortcut). It needs .NET 10,
which the SDK from [Set up once](#set-up-once) includes. Start classes from this copy: its
recordings run in the background, and a rebuild of the repo during a class never touches it.

Double-click the shortcut for the menu. Dropping an audio or video file on the shortcut, or on
`ClassScribe.exe`, transcribes that file, as the `transcribe` command does: a dropped recording that
already has a transcript is not sent again, and the window gives the commands to make it again for
free or to transcribe it again with `--again`; a recording being recorded now is not sent either. A
window opened this way stays open at the end while there is something to read: after a dropped file,
and after any failure, such as settings ClassScribe cannot use. Press Enter, Esc or Ctrl+C to close
it. Keys pressed before `Press Enter to close` appears are ignored, so an Enter pressed during the
upload cannot close it over the result.

The published app reads `appsettings.json` and `keyterms.txt` from its own folder, so change the
copies there. Publishing again replaces them only when the copies in `src\ClassScribe` were changed
more recently.

Do not publish as a single `.exe` (`-p:PublishSingleFile=true`): Bitdefender has quarantined
single-file builds of ClassScribe as `Gen:Variant.Yogi.67941`, a false positive, while the folder
publish has never been flagged. Do not turn on trimming or Native AOT either, because NAudio reaches
Windows audio through COM interop.

## Cost and privacy

- ElevenLabs lists Scribe v2 at $0.22 per audio hour through the API, plus a surcharge of about 20% when keyterms are sent.
  A two-hour class is roughly 50 cents. Check your usage page after the first run.
- Every transcript ElevenLabs sends back is charged, even one ClassScribe could not save; that one
  is kept (see [Where things go](#where-things-go)).
- Making a transcript again from ElevenLabs' saved answer (R in the menu, or `rebuild`) is free:
  nothing is sent (see [A transcript again, for free](#a-transcript-again-for-free)).
- The audio is uploaded to ElevenLabs. Keep transcripts for your own study, in line with what your teacher allows.

## If something goes wrong

Each message starts with the time; warnings and errors also say `WARNING` or `ERROR`. Look for the
words you see.

### The settings and the menu

| What you see | What to do |
| --- | --- |
| `The mistake is on line ...` | `appsettings.json` is not valid JSON. Fix that line; most often a path needs each backslash twice (`"D:\\Classes"`) or forward slashes (`"D:/Classes"`). Then choose again, or run the command again: there is no need to restart. |
| `Nothing was started. Fix the settings above in ..., then choose again` (or `then run the command again`) | The lines above name each setting with a value ClassScribe cannot use, and what it must be. Fix them, then choose again or run the command again. Until then the menu shows the same lines at the top, under `Settings:`. |
| `appsettings.json is empty` | A save was most likely cut short. Write the settings back, or at least `{ }`; the settings it leaves out then keep their defaults. |
| `... is no longer there, so the settings stay as they were when it was last read` | `appsettings.json` was deleted or renamed while ClassScribe ran. Put it back; under `dotnet run`, a build puts the copy back. To go back to the defaults instead, start ClassScribe again without the file. |
| `... could not be read: ...` | Another program, such as OneDrive or an editor, held the settings file for longer than a moment, or Windows would not let ClassScribe read it. Close that program, then choose again, or run the command again. |
| `appsettings.json cannot be read: ...` | The file is valid JSON, but not in the shape settings take: the same setting written twice in one section, say, or something other than `{ ... }` around them all. Fix what the message names, then choose again. |
| `WARNING  src\ClassScribe\appsettings.json has changed since the last build` | Under `dotnet run`, ClassScribe reads the copy in `bin\Debug\net10.0`. Build again, or edit that copy, for the change to count. |
| `ClassScribe could not start.` and `An environment variable sets a value ClassScribe cannot use` | Change or remove the variable the message names (the setting's name, with `__` for each colon), then start ClassScribe again. |
| `ClassScribe shows its menu only when someone can type into its window` | ClassScribe was started without a command from a script, or with its input redirected. Give it a command, such as `record` or `list`; the help follows the message. |
| `Recording:StopAfterHours is 10; it must be from 0 to 9.5 (hours)`, `Recording:ContinueWithinMinutes is -5; it must be from 0 to 1440 (a day)` | The new settings are checked like the others: the message gives the range and an example. Fix the value, then choose again. |
| `ElevenLabs:ApiKey is set in the settings` | Remove the key from `appsettings.json` (or an `ElevenLabs__ApiKey` variable). It is not used: ClassScribe reads the key only from `ELEVENLABS_API_KEY`. This warning comes once, at the first choice or command. |
| `Recording:OutputFolder is inside OneDrive` | OneDrive uploads a recording again and again while it grows, and can hold files ClassScribe needs to delete. Choose a folder outside OneDrive, such as `%USERPROFILE%\ClassScribe`. This warning, too, comes once, at the first choice or command, and refuses nothing. |
| A `WARNING` line under `Recordings and transcripts` in the menu | `The drive X:\ is not available`: plug the drive in, or change `Recording:OutputFolder`. `... is a file, so recordings cannot go there`: change `Recording:OutputFolder`. `Recording:OutputFolder uses %NAME%, which is not set`: set that variable, or write the folder out in full. |

### While recording

| What you see | What to do |
| --- | --- |
| `No sound for 60 seconds` | Check that the class is playing through the device named in the message and that Windows is not muted. |
| `Could not start capturing system audio yet` | Windows is still setting up the device, one just plugged in, say. ClassScribe tries again every second by itself. |
| `uses an audio format this app cannot read`, `uses the unsupported encoding` | The device sends sound in a form ClassScribe cannot record, so the recording did not start. Choose another device with menu choice 6. |
| `Cannot capture an output device right now` | Windows has no usable output device at that moment, or its audio service is restarting. ClassScribe tries again every second and says so every 30 seconds; plug in or switch on a device. |
| `Nothing to record yet; waiting for ...`, `Still nothing to record` | The chosen device is not connected, the chosen app is not playing, or there is no output device. ClassScribe keeps looking; connect the device or start the sound, or press Q twice and choose something else with 6. |
| `Could not write the recording`, `Still cannot write the recording` | The disk is full or went away. Free some space; the recording continues by itself in a new file. After two failures in a row it tries every 30 seconds, and the audio in between is lost. |
| `The capture was held up for ...` | The recording probably has a gap there. If most of that time went on storing the audio, the disk was too slow: record to an internal drive. Otherwise the computer was very busy (close heavy programs) or went to sleep (keep it plugged in with the lid open, and set a longer sleep time in Windows' power settings). |
| `The laptop slept from ... to ...; nothing was recorded then` | The lid was closed, the power button pressed or Start > Sleep chosen, or Windows put the laptop to sleep by itself. The recording went on after the gap. During a class keep the lid open and the laptop plugged in. |
| `ClassScribe was held up from ... to ... (Windows paused it)` | Windows paused ClassScribe while the laptop stayed awake, as it may with programs to save power on battery. Nothing was recorded then. Plug the laptop in, and on battery keep `Recording:KeepScreenOn` at `OnBattery` or `Always`. |
| `No audio has arrived from "..." for ...` | The capture stopped handing over audio although it should, and ClassScribe reopened it in a new segment file. If it keeps coming back, restart the class app, or choose another device or app with menu choice 6. |
| `Windows did not accept the request to keep the laptop awake`, `... to keep the screen on` | ClassScribe records or uploads all the same, but the laptop may go to sleep if it is left alone. Keep it plugged in, with sleep set to Never in Settings > System > Power & battery. |
| `Windows has not finished changing the audio device for 20 seconds` | A sound driver stopped answering as you stopped. ClassScribe stopped without it, and everything recorded so far is kept. If it happens again, restart Windows. |
| `No audio was captured`, `Nothing audible was captured from "..."` | The recording holds no sound, or only digital silence. Nothing was sent to ElevenLabs or charged, and transcribing it again would find the same silence; `record-only` ends with exit code 1 too, and the silent file is kept. Before the next class, check what is recorded (menu choice 6, `Recording:Device` or `Recording:App`) and that Windows is not muted. |
| `Nothing audible was captured in this part; the earlier parts of ... are kept` | A continued recording's new part holds no sound, but the recording as a whole does, so it goes on to be transcribed. Check what is recorded before the next class. |
| `Still paused, for 5 minutes now; P records again` | A reminder, every 5 minutes while paused; from 10 minutes the title says `PAUSED 10 MIN` in red. Press P when the break ends. |
| `The class seems to be playing while paused; P records again` | Sound has been arriving for 30 seconds while the recording is paused. Press P, or nothing of it is recorded. |
| `Only about ... of recording fit on D:\ (... free). Free some space there before a long class` | At the start, the drive has room for less than 3 hours of recording. Free some space, or record to another folder. |
| `Only about ... of recording still fit on D:\ ...`, `Less than a minute of recording still fits on ...` | Every 5 minutes while less than 30 minutes fit, with `DISK NEARLY FULL` in the title. Free some space on that drive now, or the recording stops being saved. |
| `This recording stops by itself in 15 minutes, after 9 hours (Recording:StopAfterHours)`, `This recording stops now, after 9 hours ...` | The safety stop: ElevenLabs takes at most 10 hours in one file. Start a new recording to record more. |
| `The time given with --minutes 90 is up; stopping the recording` | What you asked for. With `record`, the transcript follows. |
| The window was closed mid-class | With the published copy the recording goes on in the background, and a minimized ClassScribe window comes back 3 seconds later; start ClassScribe, or press Enter in that window, to see it. Under `dotnet run`, or with `Recording:SeparateProcess` off, the recording ended, and the audio up to that moment is in the recording's folder: the next start offers C to continue it and T to transcribe it. |
| `Recording in this window ...`, `ClassScribe was started in a way that ends every program it starts when it ends` | The recording runs in this window, so closing it ends the recording. For a recording that outlives the window, start the published `ClassScribe.exe` (see [Set up once](#set-up-once)) with `Recording:SeparateProcess` on. |
| `Only one recording at a time: ...` | Another recording is running. Start ClassScribe (or press A in the menu) to see it, or stop it first. For one in another ClassScribe window, that window shows it; closing that window ends it. |
| `The recording "..." ended unexpectedly at about ...` | The report at the start: the background process is gone. C continues the recording, T transcribes what was recorded, F forgets it. If you did not stop it, look at Bitdefender's Notifications, or at the cause the report names. |
| `Press Enter to control the recording from this window.`, `ClassScribe opened this window by itself. Keys do nothing here until you press Enter.` | This window was opened by ClassScribe itself, so keys do nothing in it until you press Enter. |

### The background recorder and its windows

Lines that come from the background process show in every window that shows the recording, and in
the recording's `.log`; the ones about the link show in the window alone.

| What you see | What to do |
| --- | --- |
| `Recording in the background: closing this window does not stop it` | Nothing: that is the usual start. Esc leaves the view; start ClassScribe again to see it. |
| `"Kalam" is being recorded already, since ...; showing it.` | `record` named a recording that is running, so the window shows it instead of starting another. |
| `Starting the recorder once more.`, then `Recording in this window instead. ...` | The background process would not start twice. The reason is on the line: Windows' words, such as `Windows did not start the recorder: Access is denied (error 5)` or `... file contains a virus (error 225)`, which is Bitdefender's block; `The recorder (process N) did not answer within 20 seconds`; or `The recorder ended before it answered, with exit code N`, with the last lines of `recorder.log`. The class is recorded in this window, so do not close it. Before the next class, add the exception from [Set up once](#set-up-once) and look at Bitdefender's Notifications. |
| `Windows did not say whether ClassScribe runs in a job (...)`, `Windows did not let the recorder start outside the job ClassScribe runs in (...)` | ClassScribe could not make sure that closing the window would leave a recorder running, so the recording runs in this window to be safe. Start the published `ClassScribe.exe` from its shortcut. |
| `The recorder could not take its request: ...`, `The recorder could not make the pipe ClassScribe-<run id> for ClassScribe's windows (...), so it did not record`, `The recorder could not write its run file (...), so it did not record`, `The recorder could not be put together: ...` | The background process gave up before recording, and the window records the class itself after one more try. Most often `%LOCALAPPDATA%\ClassScribe` cannot be written (a full disk), or something else holds a pipe of that name. |
| `Could not take the lock that allows one recording at a time (...)`, `Could not write this recording's run file (...)` | `%LOCALAPPDATA%\ClassScribe` cannot be written. The recording goes on, but without the lock, so start no other recording until it stops, and without the run file, so an unexpected end cannot be reported. Free some space on that drive. |
| `Could not bring the run file ... up to date (...)` | The recording itself is not affected; the file is written again with the next update, and until then a start of ClassScribe may report the run as it last was. |
| `Could not look at the runs in ...` | ClassScribe cannot report unexpected ends until `%LOCALAPPDATA%\ClassScribe\runs` can be read. |
| `... lines of this recording's log were not saved to its file. ...` | The recording is complete; some lines of its `.log` are missing, for the reason given, such as a disk that did not keep up or a file another program held. |
| `Could not show a line of this log in a ClassScribe window; the line is kept in this log` | A fault in the link to the window; the recording and its `.log` are not affected. |
| `The window was closed; the recording goes on. A ClassScribe window comes back, minimized ...` | The first line of a window that came back after the last one was closed. Press Enter in it to control the recording, or leave it minimized. |
| `Opening a ClassScribe window, minimized, because ...` | The first line of a window that came back for a problem, which the rest of the line names, or because the run failed. Press Enter, then read the lines above. |
| `Could not open a ClassScribe window for this recording: Windows did not start the alert window: ... (error N). The recording goes on` | The recording is fine, but no window came back to show it. Start ClassScribe yourself to see it; if the error names a virus, check Bitdefender's Notifications. |
| `The ClassScribe window that came back was closed within 2 minutes, so a closed window brings none back again in this run` | Just a note: closing the window twice means you do not want it back for a mere close. A problem still brings one. |
| `The recorder ended unexpectedly at ...; continuing the same recording` | The background process ended without being asked, most likely stopped by Bitdefender's Advanced Threat Defense; ClassScribe continued the recording as its next part. Add the exception described in [Set up once](#set-up-once). |
| `The recorder ended unexpectedly again at ..., the second time within 10 minutes, which most likely means Bitdefender is ending it` | The recording continues in this window instead, so do not close it. Check Bitdefender's Notifications (Protection > Advanced Threat Defense) and add the exception. |
| `ClassScribe opened this window by itself, so the recording is not continued in it.` | The background process was lost, or would not start, while only the window that came back by itself showed the recording. Its audio is kept: start ClassScribe, and the report offers C to continue it and T to transcribe it. |
| `The recorder ended unexpectedly at ..., after the time --minutes gave it had run out, so the recording is not continued` | The recording was due to stop anyway. The command on the line transcribes it. |
| `The recorder ended unexpectedly while it stopped the recording ...`, `The transcript of "..." was not finished; its audio is complete` | The background process was lost after the recording had stopped, or during the upload. The audio is complete: the command on the line transcribes it. If the upload had finished, ElevenLabs may have billed it already, so look at its usage page first. |
| `Lost the connection to the recording; it carries on` | The link to the background process broke and could not be made again within 10 seconds, but the process still runs. Start ClassScribe again to see it. |
| `The recorder is not answering (process N, started HH:mm)` | The background process runs but has sent nothing for 10 seconds. W keeps waiting; S stops it through its stop event, which does not need the link; its audio so far is on disk. `The recorder is answering again.` follows when it does. |
| `<key> did not reach the recording: the connection to it is being made again. Press it again in a moment.` | The key was pressed while the link was being made again. Press it again. |
| `Reconnected to the recording.`, `Some lines of the log are no longer held here; the whole log is in ...` | The link was made again. The background process keeps its last 20,000 lines for windows; older ones are in the recording's `.log`. |
| `This recording was started by another build of ClassScribe (...)` | A window of one build cannot show a recording made by another. Start the `ClassScribe.exe` named, or press S to stop the recording; the audio so far is kept. |
| `Start ClassScribe the same way as the recording (without Run as administrator) to see it.` | A window run as administrator cannot show a recording started without, and the other way round. |
| `Asked the recording to stop; its audio so far is kept.`, `Another window asked for this recording to stop` | S was pressed in a window that could not reach the recorder; the recorder stops as after Q twice. `Could not ask the recording to stop: no recorder listens for it, so it may have ended already.` means there was nothing left to stop. |
| `Something other than ClassScribe made an event named Local\ClassScribe.Stop....` | S from another window will not work for this recording; Q pressed twice in a window that shows it still stops it. |
| `Nothing is being recorded or transcribed in the background now.`, `There is no ClassScribe run ...` | The hidden `attach` command, or A, found nothing to show. |
| `ClassScribe is being closed; the recording so far is saved` | The window of a recording running in the window was closed. The audio up to that moment is kept, and the next start offers C and T. |
| `Windows is asking to end the session (a restart or a shutdown); the recording so far is saved`, `Asked Windows to wait, and to show why: ClassScribe is recording a class` | You, or an update, started a restart, a shutdown or a sign-out. Windows' screen names ClassScribe: Cancel keeps recording; `Windows is not ending the session after all; the recording goes on` follows. Restart anyway finishes the files (`Windows is ending the session now ...`), and the next start offers to continue. |
| `Windows did not let ClassScribe watch for a restart, a shutdown or a sign-out (...)`, `Windows did not accept the request to ask first before ending the session (...)` | Recording goes on, but a restart or a shutdown will not wait for ClassScribe. Stop the recording yourself before you restart. |
| `Could not show the recording's state in the window's title`, `... on Windows Terminal's tab and taskbar button`, `... on the window's taskbar button`, `Could not flash the window's taskbar button` | Harmless, once per run: the log still says everything. |
| `No ClassScribe window was showing this recording at its end, so the recorder opens the results itself` | Nothing to do: the transcript appearing is the sign that the work is done. |

### Transcribing

| What you see | What to do |
| --- | --- |
| `No ElevenLabs API key found` | Set the key (see [Set up once](#set-up-once)), then close and reopen the terminal, or quit every VS Code window and start VS Code again. The recording is kept: transcribe it with menu choice 3. |
| `ElevenLabs rejected the API key` | Check the key in `ELEVENLABS_API_KEY`, and that it may use Speech to Text. After changing it with `setx`, close and reopen the terminal; in VS Code, quit every VS Code window and start VS Code again, because a new terminal inside a running VS Code still has the old key. |
| `does not have enough credits` (HTTP 402) | The ElevenLabs account has run out of credits. Top up or change the plan on elevenlabs.io, then transcribe with menu choice 3. |
| `is rate limiting this account` (HTTP 429) | Too many requests in a short time, or at once. ClassScribe has already tried again, with growing pauses; wait a few minutes, then use menu choice 3. |
| `ElevenLabs did not answer in time` | The time limit for the upload, the transcription and the whole answer ran out: at least `ElevenLabs:TimeoutMinutes` (60 by default), and more for a longer upload, as the `Uploading ...; time limit ... minutes` line said. The audio is untouched: transcribe again with menu choice 3. On a slow line, first lower `ElevenLabs:SlowestUploadMbps`, which gives the next try more time. |
| `... already has a transcript (...). Transcribing it again is billed again, so nothing was sent` | `transcribe` on the command line, or a file dropped on the exe, would bill that recording a second time. Run the `rebuild` command the next line gives, if there is one, to make its transcript again for free, or the `transcribe ... --again` command, to transcribe it again, billed again. In the menu, choices 3 and 4 ask R or Y instead. |
| `ElevenLabs' answer to an earlier try at ..., which has been paid for, is kept in ...` | The same for a recording whose paid answer is kept without a transcript: the `rebuild` command given lays that answer out for free, and `--again` transcribes the recording again, billed again. |
| `... is being recorded now; transcribe it once it has stopped` | That recording is still being recorded, in this window or another. Stop it first, then transcribe it. |
| `Also in this folder, without a transcript: ...` | The folder holds other recordings without a transcript, such as the first half of a class split by a crash. The command after each transcribes it, or lays out its kept answer for free. |
| `There is no ClassScribe recording in ...` | The folder holds no `.wav` file named as ClassScribe names its recordings. Give the recording's own folder, or the file itself. |
| `... holds ... of audio, and ElevenLabs takes at most 10 hours per file` | Nothing was prepared or sent. Copy some of its `.wav` files into a folder of their own and transcribe that folder, then the rest the same way. |
| `If the connection breaks like this on every try` | ElevenLabs may be turning the upload down before it has read all of it. Check the API key and the account's credits before uploading again. |
| The upload fails in another way | The audio is safe. Transcribe the recording again with menu choice 3, or, after `record` or menu choice 2, paste the command at the end of the `The audio is safe in ...` line into PowerShell. |
| `ElevenLabs' answer is kept in ...`, `The transcript could not be made, but ElevenLabs' answer is saved in ...` | ElevenLabs answered, and charged, but no transcript was made from it. Open that file: its `"text"` is the transcript's text. Transcribing again is charged again. |
| `ElevenLabs' answer could not be kept anywhere` | It was paid for, but no file could take it. Free some disk space. ElevenLabs keeps the transcript under the transcription ID in the message (see [Where things go](#where-things-go)). Transcribing again is charged again. |
| `ElevenLabs heard no speech in ...` | The transcript was saved, but it is empty, and it was charged. Check what was recorded before the next class. |
| `The transcript is saved, but the upload copy ... could not be removed` | Another program holds the copy open. The transcript is fine; delete the copy later. |
| `The upload copy ... could not be removed` | The same after a failed or cancelled try: another program holds the copy open. Delete it later; a new try replaces it once nothing holds it any more. |
| `Copy ... of the transcript of ... was taken at that moment; saving the next one instead` | Another ClassScribe window saved a transcript of the same recording at the same moment. Each is kept, under its own number; nothing to do. |
| `The header of ... says it holds ..., but the file goes on to ...` | The file comes from a recorder that stopped without finishing it. ClassScribe reads all of it; nothing to do. |
| `Skipping keyterm on line ...` | That term breaks a rule of [the glossary](#the-glossary), so it was not sent; the message says which rule. |
| `No glossary at ...` | `keyterms.txt` is not where `ElevenLabs:KeytermsFile` points. Keep it next to the app, or correct the setting. |

### Making a transcript again

| What you see | What to do |
| --- | --- |
| `Rebuilt from ... into ...; nothing was uploaded` | Done, for free. |
| `... has no saved answer from ElevenLabs yet; transcribe it first` | There is no answer to lay out yet: transcribe the recording first. |
| `... has a transcript, but its saved answer from ElevenLabs is no longer there` | The `.json` beside the transcript was deleted, so only a new transcription, billed again, can make another. |
| `... has no saved answer from ElevenLabs that became a transcript, but the answer to an earlier try ... is kept in ...` | Run the `rebuild` command the next line gives: it lays that kept answer out. |
| `... is neither a file of a ClassScribe recording nor an answer from ElevenLabs saved as .json` | Give `rebuild` a recording's folder, one of its files, or an answer saved as `.json`. |
| `... is rebuilt`, `... is rebuilt instead` | `rebuild` was given a folder: it rebuilds the transcript of the newest recording there that has a saved answer, and names each newer one it passed over, with the reason. For a paid answer kept apart, run the command the next line gives; a recording without a saved answer has to be transcribed first. |
| `Could not rebuild the transcript of ...` | The saved answer could not be read, or the new transcript could not be saved; the lines after it say why, such as `ElevenLabs answered in a form this app cannot read`. Nothing was sent, and nothing is charged. |

### The list of recordings

| What you see | What to do |
| --- | --- |
| `Leaving ... out of the list of recordings: one of its files cannot be read` | Another program holds a file of that recording open, or it may not be read. Close that program and choose 3 again. The other recordings are still listed. |
| `Leaving the folder ... out of the list of recordings: it cannot be read` | A folder in the recordings folder cannot be read, such as a link to a drive that is not plugged in. |
| `No recordings found.` and a reason | The recordings folder cannot be used; see the `WARNING` row under [The settings and the menu](#the-settings-and-the-menu). |

## Layout

```
src/ClassScribe/
    Program.cs, AppHost.cs, App.cs    start-up: the settings read and checked, then a command or the menu
    Settings/         the settings, the API key from the environment, and the file read again and
                      checked before each choice and command
    Audio/            capture, WAV writing, resampling
    Audio/Windows/    WASAPI loopback through NAudio, and the once-a-second device check
    Desktop/          keeping the laptop awake, the computer's clocks, the window's title, taskbar and
                      flash, starting processes, the job check and Windows' end of the session
                      (Windows' own calls in Desktop/Windows/)
    Recorder/         the background recorder: its host, run files, lock, logs, the watch for times
                      when ClassScribe did not run, and the window it brings back by itself
    Link/             the named pipe between the recorder and the windows that show its run
    Transcription/    ElevenLabs client (Refit), glossary, transcript layout, the upload's time limit
    Storage/          recording folders, file names, numbered transcript copies and marks
    Commands/         the menu, the command line (record, transcribe, rebuild, list, devices), the
                      report at the start, the view of a recording, and the settings check before each
tests/ClassScribe.Tests/    NUnit and Moq
```

In the tests, the transcription service, the upload preparation, the command runner and the desktop
are Moq mocks. The audio device, the keyboard, the clock, the power requests and the HTTP transport
are small hand-written stand-ins (`TestDoubles.cs` and the `TestDoubles.*.cs` beside it), because the
tests drive those step by step: deliver this audio, press P, fail now, switch device, sleep. A few
tests (the `WindowsAudio` and `WindowsPower` categories) read Windows' real device list, level
meters, power source and clocks, and make one power request that is let go at once; nothing is
played or recorded, and the computer's power settings are never changed. The `WindowsProcess`
tests reach Windows for the background recorder: they start at most a hidden `cmd.exe` that ends
at once, open named pipes and stop events named after fresh run ids, make hidden windows of the
test's own, and read the test process's own job; the end-to-end tests run the window and the
recorder in one process over temporary folders, with a simulated device and a stub standing in for
ElevenLabs. No test ever starts ClassScribe itself, records, opens a device, plays a sound or
sends anything to ElevenLabs. A test run that hangs is stopped after 10 minutes and reported as
failed (`ClassScribe.Tests.runsettings`).
