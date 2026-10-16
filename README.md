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
  Q  Quit
```

- **1 Record only** asks for a name for the recording, such as `Kalam`, then records until you press
  Q twice and keeps the recording, to transcribe later. Esc at the name goes back to the menu.
- **2 Record and transcribe** does the same, then sends the recording to ElevenLabs.
- **3 Transcribe a recent recording** lists the last nine recordings, newest first, each with its
  length and `transcript ready`, `answer kept` or `no transcript yet`; press one's number. A
  recording that has a transcript, or an answer ElevenLabs was paid for (see
  [Where things go](#where-things-go)), is only done again after you press Y, because that is
  billed again. A recording whose files cannot be read just now, because another program holds one
  open, is left out of the list with a warning; close that program and choose 3 again.
- **4 Transcribe a file** asks for a file: drag it into the window, or type its path, and press
  Enter. A WAV file, 24-bit, floating-point and surround ones included, is first reduced to a small
  16 kHz mono copy; one that another recorder never finished is read to its end, with a warning.
  Any other audio or video file, or a WAV ClassScribe cannot read, is uploaded as it is.
- **5 Open the recordings folder** shows it in Explorer.
- **6 Choose what to record** lists the output devices and the apps with sound; see below.

When a choice is done, the menu comes back, after an error too. Q or Ctrl+C leaves it.

Above the choices, the menu shows where recordings go and what is recorded. If the recordings
folder cannot be used, a `WARNING` line right under its name says why: `Recording:OutputFolder`
uses a `%VARIABLE%` that is not set, its drive is not there (an unplugged USB drive, say), or a
file stands where the folder should be. If choice 3 then finds no recordings, it says
`No recordings found.` with the same reason. Fix the setting (see [Settings](#settings)) before you
record.

### What is recorded

ClassScribe records what your computer plays, not the microphone. By default it records whichever
output device the class is playing through. It starts on the device with sound, or else on
Windows' default device, and looks at every output device once a second. When the recorded device
has been quiet for two seconds while sound has been playing elsewhere for a second, it moves there
and continues in a new segment file; the segments are joined before upload. A short notification
sound on another device does not move it.

Menu choice 6 lists the connected output devices and the apps with sound open, and pins one for the
rest of the run:

- **A device**: that device and no other, even when sound plays elsewhere. If it is not connected
  when the recording starts, ClassScribe waits for it.
- **An app**, such as `chrome` or `msedge`: that app's sound alone, whatever device it plays
  through, so a video in another browser or a notification stays out. Windows cannot tell two tabs
  of the same browser apart. If the app has no sound open yet, ClassScribe waits until it plays.

To keep a choice, set `Recording:Device` or `Recording:App` in `appsettings.json`, then start
ClassScribe again: it reads its settings when it starts.

The sound is saved in mono. On a surround device the centre channel, where films and players often
put the voice, and the surround channels are mixed in with the front ones.

### While recording

Start recording before the class and leave the window in the background. Play the class as usual,
and keep the sound on: on some sound drivers, muting Windows also silences the recording.
These keys work in the ClassScribe window:

| Key | What it does |
| --- | --- |
| Q, twice | Stops. The first Q only shows `Press Q again within 5 seconds to stop`, so a Q typed into this window by mistake changes nothing. With choice 2 the transcript follows. |
| Ctrl+C | Stops at once. Another Ctrl+C within 2 seconds counts as part of the same stop; a later one cancels the preparation and the upload that follow. After a stop with Q, the first Ctrl+C cancels them. The recording is kept either way. |
| P | Pauses, for a break. Nothing is recorded until you press P again. |
| M | Marks the moment. The mark shows in the transcript at that point, as a heading `## [00:42:13] Mark 1`. |

Those seconds, like the status line and the silence warning, count the time that has really passed,
so a change of the Windows clock neither shortens nor stretches them.

Marks are kept in the recording's `.marks.txt` file, one per line. You can type a label after a
mark's time, such as `00:42:13.250 Definition of jawhar`; the next transcript of that recording then
shows `## [00:42:13] Mark 1: Definition of jawhar`. Type labels after the class, or reload the file
in your editor before you save it: an editor that saves an older copy would drop the marks set since.

### Where things go

Recordings and transcripts go to the folder set as `Recording:OutputFolder` in `appsettings.json`,
which is `%USERPROFILE%\ClassScribe` unless you change it. Each name gets a folder, and every file of
a recording carries the name and the time the recording started, in UTC (the `Z`):

```
%USERPROFILE%\ClassScribe\
    Kalam\                                       one folder per name, shared by its recordings
        Kalam_2026-10-14_19-30-00Z.wav           the audio as captured (kept)
        Kalam_2026-10-14_19-30-00Z_part2.wav     only after a device switch, a failure or 1500 MB
        Kalam_2026-10-14_19-30-00Z.marks.txt     the marks set with M, if any
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
overwritten: transcribing a recording again adds `Kalam_2026-10-14_19-30-00Z (2).md`.

The `.upload.wav` is the smaller copy (16 kHz mono) that is sent to ElevenLabs. It is removed once
the transcript is saved, unless `Recording:KeepUploadFile` is on; if another program holds it open,
ClassScribe only warns, and you can delete it later. After a failed upload it stays. Transcribing
the recording again replaces it, but a transcribed WAV file gets a new copy for each try, so delete
the old one by hand.

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

### Without the menu

```powershell
dotnet run --project src/ClassScribe -- record "Kalam"          # record, then transcribe
dotnet run --project src/ClassScribe -- record-only "Kalam"     # record only
dotnet run --project src/ClassScribe -- transcribe "C:\path\to\recording folder or file"
```

Without a name, `record` and `record-only` ask for one. `transcribe` on a recording's folder takes
the recording started last in it; on one of its `.wav` files, that whole recording. Before they
record, `record` and `record-only` give the same warning as the menu about a recordings folder
that cannot be used, and they still record where they can.

When `record-only` ends, its last message gives the command that transcribes the recording; so does
`record` when there is no API key. When the transcript after `record` fails, or you cancel it, the
command ends the `WARNING` line that starts `The audio is safe in`, just above the final error (or
`Cancelled`). A recording with nothing audible in it gets no command, as trying again would find
the same silence. Menu choices 1 and 2 do the same. The command names the program by its full path,
so you can paste it into PowerShell as it is, from any folder.

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
- If the disk cannot be written, ClassScribe says so and continues in a new file. If writing fails
  again straight away (a full disk, say), it waits 30 seconds before each new try, and the audio of
  that time is lost. A file left with no audio in it is removed. Only audio that reached the disk is
  counted, so the marks stay in step with what is transcribed.
- If Windows holds the capture up for longer than it keeps audio waiting (one second), ClassScribe
  warns that the recording probably has a gap there.
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

After changing the glossary you can transcribe an old recording again (menu choice 3).
That is billed as a new transcription.

## Settings

`appsettings.json` holds the settings. With `dotnet run`, edit `src\ClassScribe\appsettings.json`
(each build copies it next to the app); a published app reads the copy beside `ClassScribe.exe`.
ClassScribe reads the file once, when it starts, and checks it before it records or uploads
anything. A value it cannot use stops it at once, with a message that names the setting and says
what it must be; fix it and start ClassScribe again. A change made while ClassScribe is running
counts from the next start, so close it and start it again after editing.

The file is JSON, which has two traps:

- In a path, write each backslash twice, as in `"D:\\Classes"`, or use forward slashes, as in
  `"D:/Classes"`. A single backslash starts a special character, such as `\t` for a tab.
- Write numbers with a dot (`1.5`, not `1,5`), and on or off as `true` or `false`.

The ones you are most likely to change:

| Setting | Default | Meaning |
| --- | --- | --- |
| `Recording:OutputFolder` | `%USERPROFILE%\ClassScribe` | Where recordings and transcripts go, one folder per name. Environment variables work, and quotes around the path are ignored. A relative path is taken from your user folder, so `Classes` means `%USERPROFILE%\Classes`. |
| `Recording:Device` | empty | Part of the name of the output device to record, such as `Pixel Buds`. Empty means the device the class plays on. |
| `Recording:App` | empty | The app whose sound to record, by its process name, such as `chrome`. Overrides `Recording:Device`. |
| `Recording:SilenceWarningSeconds` | 60 | Warn after this much silence. At least 1. |
| `Recording:KeepUploadFile` | false | Keep the 16 kHz copy that was uploaded. |
| `Recording:OpenFolderWhenDone` | true | Open the folder in Explorer when the recording or transcript is ready. |
| `Recording:OpenTranscriptWhenDone` | true | Open the transcript when it is ready. |
| `ElevenLabs:LanguageCode` | empty | Empty lets ElevenLabs detect the language. |
| `ElevenLabs:NoVerbatim` | false | Drop filler words and false starts. |
| `ElevenLabs:KeytermsFile` | `keyterms.txt` | The glossary. A relative path is taken from the app's folder; environment variables work, and quotes are ignored. Empty sends no glossary. |
| `ElevenLabs:TimeoutMinutes` | 60 | Longest time one try may take: the upload, the transcription and the whole answer. From 1 to 1440. |
| `Transcript:IncludeTimestamps` | true | Start paragraphs with `[hh:mm:ss]`. |
| `Transcript:ParagraphPauseSeconds` | 2.0 | A pause between words this long, in seconds, starts a new paragraph. 0 or more; 0 turns these breaks off. |
| `Transcript:MaxParagraphCharacters` | 900 | A longer paragraph is split at the next sentence end. 0 or more; 0 turns this off. |
| `Logging:Console:FormatterName` | `plain` | One line per message. Set to `simple` to see full stack traces. |

The file has a few more that rarely need changing, checked the same way: `Recording:FlushSeconds`
(5, at least 1), `Recording:StatusIntervalSeconds` (60, at least 1), `Recording:MaxSegmentMegabytes`
(1500, from 10 to 4000), `ElevenLabs:ModelId` (`scribe_v2`, not empty), `ElevenLabs:TagAudioEvents`
(false; true marks laughter and other sounds), `ElevenLabs:MaxAttempts` (3, from 1 to 10) and
`ElevenLabs:RetryDelaySeconds` (5, from 0 to 3600).

The API key is deliberately not in this file. It is read from `ELEVENLABS_API_KEY` alone: a key
written into `appsettings.json` is ignored, and ClassScribe warns you to remove it, so that it
cannot be shared by mistake.

## A program to double-click

Publish ClassScribe as a folder:

```powershell
dotnet publish src/ClassScribe -c Release -r win-x64 --self-contained false -o publish
```

Keep `publish\ClassScribe.exe` together with the files beside it, and make a shortcut to it:
right-click `ClassScribe.exe`, then Show more options, Send to, Desktop (create shortcut). It needs
.NET 10, which the SDK from [Set up once](#set-up-once) includes.

Double-click the shortcut for the menu. Dropping an audio or video file on the shortcut, or on
`ClassScribe.exe`, transcribes that file. A window opened this way stays open at the end while there
is something to read: after a dropped file, and after any failure, such as settings ClassScribe
cannot use. Press Enter, Esc or Ctrl+C to close it. Keys pressed before `Press Enter to close`
appears are ignored, so an Enter pressed during the upload cannot close it over the result.

The published app reads `appsettings.json` and `keyterms.txt` from its own folder, so change the
copies there. Publishing again replaces them only when the copies in `src\ClassScribe` were changed
more recently.

A single `.exe` is also possible: add `-p:PublishSingleFile=true` to the command, and keep
`appsettings.json` and `keyterms.txt` beside it. Bitdefender has quarantined earlier single-file
builds of ClassScribe as `Gen:Variant.Yogi.67941`. That was a false positive, but it is why the
folder is recommended.

Do not turn on trimming or Native AOT, because NAudio reaches Windows audio through COM interop.

## Cost and privacy

- ElevenLabs lists Scribe v2 at $0.22 per audio hour through the API, plus a surcharge of about 20% when keyterms are sent.
  A two-hour class is roughly 50 cents. Check your usage page after the first run.
- Every transcript ElevenLabs sends back is charged, even one ClassScribe could not save; that one
  is kept (see [Where things go](#where-things-go)).
- The audio is uploaded to ElevenLabs. Keep transcripts for your own study, in line with what your teacher allows.

## If something goes wrong

Each message starts with the time; warnings and errors also say `WARNING` or `ERROR`. Look for the
words you see.

### When ClassScribe starts

| What you see | What to do |
| --- | --- |
| `ClassScribe could not start.` and `The mistake is on line ...` | `appsettings.json` is not valid JSON. Fix that line; most often a path needs each backslash twice (`"D:\\Classes"`) or forward slashes (`"D:/Classes"`). |
| `Nothing was started. Fix the settings above in appsettings.json` | The lines above name each setting with a value ClassScribe cannot use, and what it must be. Fix them and start again. |
| `ElevenLabs:ApiKey is set in the settings` | Remove the key from `appsettings.json` (or an `ElevenLabs__ApiKey` variable). It is not used: ClassScribe reads the key only from `ELEVENLABS_API_KEY`. |
| A `WARNING` line under `Recordings and transcripts` in the menu | `The drive X:\ is not available`: plug the drive in, or change `Recording:OutputFolder`. `... is a file, so recordings cannot go there`: change `Recording:OutputFolder`. `Recording:OutputFolder uses %NAME%, which is not set`: set that variable, or write the folder out in full. |

### While recording

| What you see | What to do |
| --- | --- |
| `No sound for 60 seconds` | Check that the class is playing through the device named in the message and that Windows is not muted. |
| `Could not start capturing system audio yet` | Windows is still setting up the device, one just plugged in, say. ClassScribe tries again every second by itself. |
| `uses an audio format this app cannot read`, `uses the unsupported encoding` | The device sends sound in a form ClassScribe cannot record, so the recording did not start. Choose another device with menu choice 6. |
| `Cannot capture an output device right now` | Windows has no usable output device at that moment. ClassScribe tries again every second and says so every 30 seconds; plug in or switch on a device. |
| `Nothing to record yet; waiting for ...`, `Still nothing to record` | The chosen device is not connected, the chosen app is not playing, or there is no output device. ClassScribe keeps looking; connect the device or start the sound, or press Q twice and choose something else with 6. |
| `Could not write the recording`, `Still cannot write the recording` | The disk is full or went away. Free some space; the recording continues by itself in a new file. After two failures in a row it tries every 30 seconds, and the audio in between is lost. |
| `The capture was held up for ...` | The recording probably has a gap there. If most of that time went on storing the audio, the disk was too slow: record to an internal drive. Otherwise the computer was very busy (close heavy programs) or went to sleep (keep it plugged in with the lid open, and set a longer sleep time in Windows' power settings). |
| `Windows has not finished changing the audio device for 20 seconds` | A sound driver stopped answering as you stopped. ClassScribe stopped without it, and everything recorded so far is kept. If it happens again, restart Windows. |
| `No audio was captured`, `Nothing audible was captured from "..."` | The recording holds no sound, or only digital silence. Nothing was sent to ElevenLabs or charged, and transcribing it again would find the same silence. Before the next class, check what is recorded (menu choice 6, `Recording:Device` or `Recording:App`) and that Windows is not muted. |
| The window was closed mid-class | The audio up to that moment is in the recording's folder. Transcribe it with menu choice 3. |

### Transcribing

| What you see | What to do |
| --- | --- |
| `No ElevenLabs API key found` | Set the key (see [Set up once](#set-up-once)), then close and reopen the terminal, or quit every VS Code window and start VS Code again. The recording is kept: transcribe it with menu choice 3. |
| `ElevenLabs rejected the API key` | Check the key in `ELEVENLABS_API_KEY`, and that it may use Speech to Text. After changing it with `setx`, close and reopen the terminal; in VS Code, quit every VS Code window and start VS Code again, because a new terminal inside a running VS Code still has the old key. |
| `does not have enough credits` (HTTP 402) | The ElevenLabs account has run out of credits. Top up or change the plan on elevenlabs.io, then transcribe with menu choice 3. |
| `is rate limiting this account` (HTTP 429) | Too many requests in a short time, or at once. ClassScribe has already tried again, with growing pauses; wait a few minutes, then use menu choice 3. |
| `ElevenLabs did not answer in time` | The time limit, `ElevenLabs:TimeoutMinutes` (60 by default), covers the upload, the transcription and the whole answer, and it ran out. The audio is untouched: transcribe again with menu choice 3, with a longer limit for a very long class. |
| `If the connection breaks like this on every try` | ElevenLabs may be turning the upload down before it has read all of it. Check the API key and the account's credits before uploading again. |
| The upload fails in another way | The audio is safe. Transcribe the recording again with menu choice 3, or, after `record` or menu choice 2, paste the command at the end of the `The audio is safe in ...` line into PowerShell. |
| `ElevenLabs' answer is kept in ...`, `The transcript could not be made, but ElevenLabs' answer is saved in ...` | ElevenLabs answered, and charged, but no transcript was made from it. Open that file: its `"text"` is the transcript's text. Transcribing again is charged again. |
| `ElevenLabs' answer could not be kept anywhere` | It was paid for, but no file could take it. Free some disk space. ElevenLabs keeps the transcript under the transcription ID in the message (see [Where things go](#where-things-go)). Transcribing again is charged again. |
| `ElevenLabs heard no speech in ...` | The transcript was saved, but it is empty, and it was charged. Check what was recorded before the next class. |
| `The transcript is saved, but the upload copy ... could not be removed` | Another program holds the copy open. The transcript is fine; delete the copy later. |
| `The header of ... says it holds ..., but the file goes on to ...` | The file comes from a recorder that stopped without finishing it. ClassScribe reads all of it; nothing to do. |
| `Skipping keyterm on line ...` | That term breaks a rule of [the glossary](#the-glossary), so it was not sent; the message says which rule. |
| `No glossary at ...` | `keyterms.txt` is not where `ElevenLabs:KeytermsFile` points. Keep it next to the app, or correct the setting. |

### The list of recordings

| What you see | What to do |
| --- | --- |
| `Leaving ... out of the list of recordings: one of its files cannot be read` | Another program holds a file of that recording open, or it may not be read. Close that program and choose 3 again. The other recordings are still listed. |
| `Leaving the folder ... out of the list of recordings: it cannot be read` | A folder in the recordings folder cannot be read, such as a link to a drive that is not plugged in. |
| `No recordings found.` and a reason | The recordings folder cannot be used; see the `WARNING` row under [When ClassScribe starts](#when-classscribe-starts). |

## Layout

```
src/ClassScribe/
    Program.cs, AppHost.cs, App.cs    start-up: settings read and checked, then a command or the menu
    Settings/         the settings, the API key from the environment, and the check at start
    Audio/            capture, WAV writing, resampling
    Audio/Windows/    the only Windows-specific code (WASAPI loopback through NAudio)
    Transcription/    ElevenLabs client (Refit), glossary, transcript layout
    Storage/          recording folders, file names and marks
    Commands/         the menu, record and transcribe
tests/ClassScribe.Tests/    NUnit and Moq
```

In the tests, the transcription service, the upload preparation, the command runner and the desktop
are Moq mocks. The audio device, the keyboard, the clock and the HTTP transport are small hand-written
stand-ins (`TestDoubles.cs`), because the tests drive those step by step: deliver this audio, press P,
fail now, switch device. A test run that hangs is stopped after 10 minutes and reported as failed
(`ClassScribe.Tests.runsettings`).
